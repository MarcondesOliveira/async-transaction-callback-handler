"""Dispatch the central deployment and propagate its exact run's final result."""

import json
import os
import re
import subprocess
import time


REPOSITORY = "MarcondesOliveira/async-transaction-infrastructure"
SERVICE = "async-transaction-callback-handler"


def identity_mismatches(run, run_id, commit):
    expected = {"id": run_id, "event": "workflow_dispatch", "head_branch": "main",
                "path": ".github/workflows/deploy.yml", "repository": REPOSITORY,
                "display_title": f"deploy {SERVICE} @ {commit}"}
    observed = {key: run.get(key) for key in expected}
    observed["repository"] = (run.get("repository") or {}).get("full_name")
    observed["path"] = (run.get("path") or "").split("@")[0]
    return {key: {"expected": value, "observed": observed[key]}
            for key, value in expected.items() if observed[key] != value}


def api(path, payload=None):
    command = ["gh", "api", "-H", "X-GitHub-Api-Version: 2022-11-28", path]
    if payload is not None:
        command += ["--method", "POST", "--input", "-"]
    result = subprocess.run(command, input=json.dumps(payload) if payload else None,
                            text=True, capture_output=True, timeout=60)
    if result.returncode:
        # Do not print tokens, headers or environment values on authentication failure.
        raise RuntimeError("GitHub API request failed; check token expiry and infrastructure Actions permissions")
    return json.loads(result.stdout)


def main():
    if not os.environ.get("GH_TOKEN"):
        raise ValueError("INFRA_WORKFLOW_TOKEN is missing")
    commit = os.environ.get("SOURCE_COMMIT", "")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Source commit must be a full lowercase SHA")
    dispatched = api(f"repos/{REPOSITORY}/actions/workflows/deploy.yml/dispatches", {
        "ref": "main", "return_run_details": True,
        "inputs": {"service": SERVICE, "source_commit": commit, "operation": "deploy"},
    })
    run_id = dispatched.get("workflow_run_id")
    if type(run_id) is not int or run_id <= 0:
        raise RuntimeError("Dispatch did not return an exact workflow run ID; inspect infrastructure Actions before retrying")
    url = f"https://github.com/{REPOSITORY}/actions/runs/{run_id}"
    print(f"Central deployment: {url}", flush=True)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write(f"\nCentral deployment: [run {run_id}]({url})\nSource commit: `{commit}`\n")
    started = time.monotonic()
    deadline = started + 40 * 60
    while (now := time.monotonic()) < deadline:
        run = api(f"repos/{REPOSITORY}/actions/runs/{run_id}")
        mismatches = identity_mismatches(run, run_id, commit)
        if mismatches:
            # Only known early metadata gaps/title evaluation receive a short grace
            # period. A wrong run ID or concrete repository/ref/workflow is fatal.
            transient = all(key != "id" and
                            (key == "display_title" or item["observed"] in (None, ""))
                            for key, item in mismatches.items())
            diagnostic = json.dumps(mismatches, ensure_ascii=True)
            if transient and run.get("status") != "completed" and now < started + 120:
                print(f"Waiting for central identity metadata: {diagnostic}", flush=True)
                time.sleep(15)
                continue
            raise RuntimeError(f"Central workflow identity mismatch: {diagnostic}; inspect {url}")
        if run.get("status") == "completed":
            if run.get("conclusion") != "success":
                raise RuntimeError(f"Central deployment did not succeed: {run.get('conclusion')}; inspect {url}")
            print("Central Terraform deployment, Lambda verification and end-to-end smoke succeeded.")
            return
        time.sleep(15)
    raise TimeoutError(f"Central deployment wait expired; inspect {url} before retrying. It was not cancelled.")


if __name__ == "__main__":
    main()
