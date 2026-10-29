#!/usr/bin/env bash
set -euo pipefail

commit_sha="${GITHUB_SHA:?Set GITHUB_SHA to the source commit}"
output_dir="${1:?Provide a new absolute output directory}"

if [[ ! "$commit_sha" =~ ^[0-9a-f]{40}$ ]]; then
  echo "GITHUB_SHA must be a full commit SHA." >&2
  exit 1
fi
if [[ "$output_dir" != /* || -e "$output_dir" ]]; then
  echo "Output must be an absolute directory that does not exist yet." >&2
  exit 1
fi
if [[ "$(git rev-parse HEAD)" != "$commit_sha" ]]; then
  echo "GITHUB_SHA does not match the checked-out commit." >&2
  exit 1
fi

publish_dir="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/callback-handler-publish.XXXXXX")"
dotnet publish src/MarcondesOliveira.AsyncTransactions.CallbackHandler.Host/MarcondesOliveira.AsyncTransactions.CallbackHandler.Host.csproj \
  --configuration Release --runtime linux-arm64 --self-contained false \
  --disable-build-servers -m:1 -p:UseAppHost=false -p:PublishReadyToRun=false \
  --output "$publish_dir"

for required in AsyncCallback.Handler.dll \
  AsyncCallback.Handler.deps.json \
  AsyncCallback.Handler.runtimeconfig.json; do
  test -s "$publish_dir/$required"
done

mkdir -p "$output_dir"
package_name="async-transaction-callback-handler.zip"
(
  cd "$publish_dir"
  zip -q -r "$output_dir/$package_name" .
)
unzip -tq "$output_dir/$package_name"
(
  cd "$output_dir"
  sha256sum "$package_name" > "$package_name.sha256"
  sha256sum --check "$package_name.sha256"
)
sha256_hex="$(sha256sum "$output_dir/$package_name" | cut -d ' ' -f 1)"
sha256_base64="$(openssl dgst -sha256 -binary "$output_dir/$package_name" | base64 -w0)"

jq -n --arg service async-transaction-callback-handler --arg commit "$commit_sha" \
  --arg package "$package_name" --arg sha256 "$sha256_hex" \
  --arg source_code_hash "$sha256_base64" --arg sdk "$(dotnet --version)" \
  '{service: $service, commit: $commit, package: $package,
    s3Key: ($service + "/" + $commit + "/" + $package),
    sha256: $sha256, sourceCodeHash: $source_code_hash, sdkVersion: $sdk,
    runtime: "dotnet10", architecture: "arm64", selfContained: false}' \
  > "$output_dir/build-manifest.json"

echo "Package and checksum created for commit $commit_sha. No AWS upload or deployment performed."
