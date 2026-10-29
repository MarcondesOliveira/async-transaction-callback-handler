#!/usr/bin/env bash
set -euo pipefail

artifact_dir="${1:?Provide the downloaded artifact directory}"
mode="${2:---publish}"
source_commit="${SOURCE_COMMIT:?Set SOURCE_COMMIT from the verified CI run}"
service="async-transaction-callback-handler"
package="$service.zip"

if [[ ! "$source_commit" =~ ^[0-9a-f]{40}$ || ! -d "$artifact_dir" ]]; then
  echo "Invalid source commit or artifact directory." >&2
  exit 1
fi
if [[ "$mode" != --verify-only && "$mode" != --publish ]]; then
  echo "Use --verify-only or --publish." >&2
  exit 1
fi
artifact_dir="$(realpath "$artifact_dir")"
key="$service/$source_commit/$package"
manifest="$artifact_dir/build-manifest.json"

jq -e --arg service "$service" --arg commit "$source_commit" \
  --arg package "$package" --arg key "$key" '
    .service == $service and .commit == $commit and .package == $package and
    .s3Key == $key and .runtime == "dotnet10" and .architecture == "arm64" and
    .selfContained == false and (.sha256 | test("^[0-9a-f]{64}$")) and
    (.sourceCodeHash | test("^[A-Za-z0-9+/]{43}=$"))
  ' "$manifest" > /dev/null

sha256_hex="$(sha256sum "$artifact_dir/$package" | cut -d ' ' -f 1)"
sha256_base64="$(openssl dgst -sha256 -binary "$artifact_dir/$package" | base64 -w0)"
test "$sha256_hex" = "$(jq -r '.sha256' "$manifest")"
test "$sha256_base64" = "$(jq -r '.sourceCodeHash' "$manifest")"
test "$(cat "$artifact_dir/$package.sha256")" = "$sha256_hex  $package"
unzip -tq "$artifact_dir/$package"
entries="$(unzip -Z1 "$artifact_dir/$package")"
for required in AsyncCallback.Handler.dll \
  AsyncCallback.Handler.deps.json \
  AsyncCallback.Handler.runtimeconfig.json; do
  found=false
  while IFS= read -r entry; do
    if [[ "$entry" == "$required" ]]; then
      found=true
      break
    fi
  done <<< "$entries"
  if [[ "$found" != true ]]; then
    echo "Missing required ZIP root entry: $required" >&2
    exit 1
  fi
done

if [[ "$mode" == --verify-only ]]; then
  echo "Artifact identity, target, ZIP and checksums verified. No AWS request made."
  exit 0
fi

: "${ARTIFACT_BUCKET:?Set ARTIFACT_BUCKET}"
: "${AWS_ACCOUNT_ID:?Set AWS_ACCOUNT_ID}"
: "${AWS_REGION:?Set AWS_REGION}"
if [[ ! "$AWS_ACCOUNT_ID" =~ ^[0-9]{12}$ ]]; then
  echo "Invalid expected AWS account." >&2
  exit 1
fi
test "$(aws sts get-caller-identity --query Account --output text)" = "$AWS_ACCOUNT_ID"

aws s3api put-object --bucket "$ARTIFACT_BUCKET" --key "$key" \
  --body "$artifact_dir/$package" --if-none-match '*' \
  --checksum-algorithm SHA256 --checksum-sha256 "$sha256_base64" \
  --server-side-encryption AES256 --expected-bucket-owner "$AWS_ACCOUNT_ID" \
  --metadata "source-commit=$source_commit,service=$service" \
  --output json > "$artifact_dir/publish-receipt.json"
test "$(jq -r '.ChecksumSHA256' "$artifact_dir/publish-receipt.json")" = "$sha256_base64"

for companion in build-manifest.json "$package.sha256"; do
  aws s3api put-object --bucket "$ARTIFACT_BUCKET" \
    --key "$service/$source_commit/$companion" --body "$artifact_dir/$companion" \
    --if-none-match '*' --checksum-algorithm SHA256 --server-side-encryption AES256 \
    --expected-bucket-owner "$AWS_ACCOUNT_ID" --output json > /dev/null
done

aws s3api head-object --bucket "$ARTIFACT_BUCKET" --key "$key" \
  --checksum-mode ENABLED --expected-bucket-owner "$AWS_ACCOUNT_ID" \
  --output json > "$artifact_dir/published-object.json"
jq -e --arg checksum "$sha256_base64" --arg commit "$source_commit" '
  .ChecksumSHA256 == $checksum and .ServerSideEncryption == "AES256" and
  .Metadata["source-commit"] == $commit
  ' "$artifact_dir/published-object.json" > /dev/null

echo "Versioned Callback Handler artifact published and verified. No Lambda or Terraform deployment performed."
