import importlib.util
import os
from pathlib import Path
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location(
    "deploy", Path(__file__).resolve().parents[2] / ".github/scripts/deploy.py")
deploy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deploy)
COMMIT = "a" * 40


def result(conclusion="success", status="completed"):
    return {"id": 123, "event": "workflow_dispatch", "head_branch": "main",
            "path": ".github/workflows/deploy.yml",
            "repository": {"full_name": deploy.REPOSITORY},
            "display_title": f"deploy {deploy.SERVICE} @ {COMMIT}",
            "status": status, "conclusion": conclusion}


class DeployTests(unittest.TestCase):
    def setUp(self):
        env = patch.dict(os.environ, {"GH_TOKEN": "test-only", "SOURCE_COMMIT": COMMIT}, clear=True)
        env.start()
        self.addCleanup(env.stop)

    def test_exact_run_success(self):
        with patch.object(deploy, "api", side_effect=[{"workflow_run_id": 123}, result()]) as api:
            deploy.main()
        payload = api.call_args_list[0].args[1]
        self.assertTrue(payload["return_run_details"])
        self.assertEqual(payload["inputs"]["source_commit"], COMMIT)
        self.assertEqual(payload["inputs"]["operation"], "deploy")
        self.assertEqual(payload["inputs"]["service"], "async-transaction-callback-handler")

    def test_failure_cancelled_and_skipped_are_failures(self):
        for conclusion in ["failure", "cancelled", "skipped", "timed_out"]:
            with self.subTest(conclusion=conclusion), patch.object(
                    deploy, "api", side_effect=[{"workflow_run_id": 123}, result(conclusion)]):
                with self.assertRaisesRegex(RuntimeError, "did not succeed"):
                    deploy.main()

    def test_identity_mismatch_rejected(self):
        for field, value in [("id", 999), ("head_branch", "other"),
                             ("display_title", "other commit"), ("path", "other.yml"),
                             ("repository", {"full_name": "other/repo"}), ("event", "push")]:
            run = result()
            run[field] = value
            with self.subTest(field=field), patch.object(
                    deploy, "api", side_effect=[{"workflow_run_id": 123}, run]):
                with self.assertRaisesRegex(RuntimeError, "identity"):
                    deploy.main()

    def test_missing_run_id_rejected(self):
        with patch.object(deploy, "api", return_value={}):
            with self.assertRaisesRegex(RuntimeError, "exact workflow run ID"):
                deploy.main()

    def test_missing_token_or_bad_commit_never_dispatch(self):
        for field, value in [("GH_TOKEN", ""), ("SOURCE_COMMIT", "invalid")]:
            with self.subTest(field=field), patch.dict(os.environ, {field: value}), patch.object(deploy, "api") as api:
                with self.assertRaises(ValueError):
                    deploy.main()
                api.assert_not_called()

    def test_waits_for_completion(self):
        with patch.object(deploy, "api", side_effect=[{"workflow_run_id": 123}, result(None, "queued"), result()]), patch.object(deploy.time, "sleep") as sleep:
            deploy.main()
            sleep.assert_called_once_with(15)

    def test_timeout_does_not_cancel_or_redispatch(self):
        with patch.object(deploy, "api", return_value={"workflow_run_id": 123}) as api, patch.object(deploy.time, "monotonic", side_effect=[0, 2401]):
            with self.assertRaises(TimeoutError):
                deploy.main()
            self.assertEqual(api.call_count, 1)

    def test_initial_title_or_missing_metadata_can_settle(self):
        for field, value in [("display_title", "Deploy service"), ("path", None),
                             ("repository", None), ("event", None), ("head_branch", None)]:
            initial = result(None, "queued")
            initial[field] = value
            with self.subTest(field=field), patch.object(
                    deploy, "api", side_effect=[{"workflow_run_id": 123}, initial, result()]) as api, patch.object(deploy.time, "sleep") as sleep:
                deploy.main()
                sleep.assert_called_once_with(15)
                self.assertEqual(api.call_count, 3)

    def test_unsettled_metadata_has_bounded_grace_and_diagnostic(self):
        initial = result(None, "queued")
        initial["display_title"] = "Deploy service"
        with patch.object(deploy, "api", side_effect=[{"workflow_run_id": 123}, initial, initial]) as api, patch.object(deploy.time, "sleep"), patch.object(deploy.time, "monotonic", side_effect=[0, 0, 121]):
            with self.assertRaisesRegex(RuntimeError, 'display_title.*observed.*Deploy service'):
                deploy.main()
            self.assertEqual(api.call_count, 3)

    def test_concrete_wrong_identity_fails_immediately_even_queued(self):
        for field, value in [("id", 999), ("repository", {"full_name": "other/repo"}),
                             ("path", "wrong.yml"), ("head_branch", "other"), ("event", "push")]:
            initial = result(None, "queued")
            initial[field] = value
            with self.subTest(field=field), patch.object(
                    deploy, "api", side_effect=[{"workflow_run_id": 123}, initial]), patch.object(deploy.time, "sleep") as sleep:
                with self.assertRaisesRegex(RuntimeError, field):
                    deploy.main()
                sleep.assert_not_called()

    def test_completed_success_never_bypasses_identity_validation(self):
        initial = result()
        initial["display_title"] = "Deploy service"
        with patch.object(deploy, "api", side_effect=[{"workflow_run_id": 123}, initial]), patch.object(deploy.time, "sleep") as sleep:
            with self.assertRaisesRegex(RuntimeError, "display_title"):
                deploy.main()
            sleep.assert_not_called()


if __name__ == "__main__":
    unittest.main()
