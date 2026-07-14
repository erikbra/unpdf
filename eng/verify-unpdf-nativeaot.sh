#!/usr/bin/env bash
set -euo pipefail

rid="${1:-linux-x64}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/apps/PdfBox.Net.Unpdf/PdfBox.Net.Unpdf.csproj"
fixtures=(
  "$root/tests/SharedFixtures/classic-xref-fixture.pdf"
  "$root/tests/SharedFixtures/encrypted-owner-restricted.pdf"
  "$root/tests/SharedFixtures/with_outline.pdf"
)
artifact_root="$root/artifacts/unpdf-nativeaot/$rid"
managed_dir="$artifact_root/managed"
aot_dir="$artifact_root/aot"
managed_html="$artifact_root/managed-html"
aot_html="$artifact_root/aot-html"

rm -rf "$artifact_root"
mkdir -p "$artifact_root"

measure() {
  local timing_file="$1"
  local diagnostics_file="${timing_file%.txt}.log"
  shift
  TIMEFORMAT='%3R'
  { time "$@" 2>"$diagnostics_file"; } 2>"$timing_file"
}

file_size() {
  if stat -f '%z' "$1" >/dev/null 2>&1; then
    stat -f '%z' "$1"
  else
    stat -c '%s' "$1"
  fi
}

measure "$artifact_root/managed-publish-seconds.txt" \
  dotnet publish "$project" -c Release -r "$rid" -p:PublishProfile=SingleFile -o "$managed_dir"
measure "$artifact_root/aot-publish-seconds.txt" \
  dotnet publish "$project" -c Release -r "$rid" -p:PublishProfile=NativeAot -o "$aot_dir"

executable_name="unpdf"
if [[ "$rid" == win-* ]]; then
  executable_name="unpdf.exe"
fi
managed_executable="$managed_dir/$executable_name"
aot_executable="$aot_dir/$executable_name"
test -x "$managed_executable" || [[ "$rid" == win-* ]]
test -x "$aot_executable" || [[ "$rid" == win-* ]]

convert_fixtures() {
  local executable="$1"
  local output_root="$2"
  local fixture
  for fixture in "${fixtures[@]}"; do
    "$executable" "$fixture" --output "$output_root/$(basename "$fixture" .pdf)" --quiet
  done
}

measure "$artifact_root/managed-conversion-seconds.txt" \
  convert_fixtures "$managed_executable" "$managed_html"
measure "$artifact_root/aot-conversion-seconds.txt" \
  convert_fixtures "$aot_executable" "$aot_html"
diff -qr "$managed_html" "$aot_html"

managed_bytes="$(file_size "$managed_executable")"
aot_bytes="$(file_size "$aot_executable")"
size_change="$(awk -v managed="$managed_bytes" -v aot="$aot_bytes" 'BEGIN { printf "%.1f", (aot / managed - 1) * 100 }')"

cat >"$artifact_root/report.md" <<EOF
# unpdf NativeAOT report ($rid)

| Measurement | Managed single file | NativeAOT |
|---|---:|---:|
| Executable | $managed_bytes bytes | $aot_bytes bytes |
| Publish | $(cat "$artifact_root/managed-publish-seconds.txt") s | $(cat "$artifact_root/aot-publish-seconds.txt") s |
| Fixture conversion | $(cat "$artifact_root/managed-conversion-seconds.txt") s | $(cat "$artifact_root/aot-conversion-seconds.txt") s |

NativeAOT executable size change versus the managed single file: $size_change%.
Both executables produced byte-identical output for all fixtures.
EOF

cat "$artifact_root/report.md"
