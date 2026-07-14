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
artifact_root="$root/artifacts/unpdf-publish/$rid"
baseline_dir="$artifact_root/baseline"
single_dir="$artifact_root/single-file"
baseline_html="$artifact_root/baseline-html"
single_html="$artifact_root/single-file-html"

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

measure "$artifact_root/baseline-publish-seconds.txt" \
  dotnet publish "$project" -c Release -r "$rid" --self-contained true -o "$baseline_dir"
measure "$artifact_root/single-file-publish-seconds.txt" \
  dotnet publish "$project" -c Release -r "$rid" -p:PublishProfile=SingleFile -o "$single_dir"

single_executable="$single_dir/unpdf"
if [[ "$rid" == win-* ]]; then
  single_executable="$single_dir/unpdf.exe"
fi

runtime_file_count="$(find "$single_dir" -maxdepth 1 -type f ! -name '*.pdb' | wc -l | tr -d ' ')"
runtime_published_file="$(find "$single_dir" -maxdepth 1 -type f ! -name '*.pdb' -print -quit)"
if [[ "$runtime_file_count" -ne 1 || "$runtime_published_file" != "$single_executable" ]]; then
  printf 'Expected exactly one runtime file in %s, found:\n' "$single_dir" >&2
  find "$single_dir" -maxdepth 1 -type f ! -name '*.pdb' -print >&2
  exit 1
fi

baseline_executable="$baseline_dir/unpdf"
if [[ "$rid" == win-* ]]; then
  baseline_executable="$baseline_dir/unpdf.exe"
fi

convert_fixtures() {
  local executable="$1"
  local output_root="$2"
  local fixture
  for fixture in "${fixtures[@]}"; do
    "$executable" "$fixture" \
      --output "$output_root/$(basename "$fixture" .pdf)" \
      --quiet
  done
}

measure "$artifact_root/baseline-conversion-seconds.txt" \
  convert_fixtures "$baseline_executable" "$baseline_html"
measure "$artifact_root/single-file-conversion-seconds.txt" \
  convert_fixtures "$single_executable" "$single_html"
diff -qr "$baseline_html" "$single_html"

baseline_bytes="$(du -sk "$baseline_dir" | awk '{ print $1 * 1024 }')"
single_bytes="$(file_size "$single_executable")"
archive="$artifact_root/unpdf-$rid.tar.gz"
tar -C "$single_dir" -czf "$archive" "$(basename "$single_executable")"
archive_bytes="$(file_size "$archive")"
reduction="$(awk -v baseline="$baseline_bytes" -v single="$single_bytes" 'BEGIN { printf "%.1f", (1 - single / baseline) * 100 }')"

cat >"$artifact_root/size-report.md" <<EOF
# unpdf single-file report ($rid)

| Measurement | Value |
|---|---:|
| Untrimmed self-contained directory | $baseline_bytes bytes |
| Trimmed compressed executable | $single_bytes bytes |
| Compressed release archive | $archive_bytes bytes |
| Executable reduction | $reduction% |
| Baseline publish | $(cat "$artifact_root/baseline-publish-seconds.txt") s |
| Single-file publish | $(cat "$artifact_root/single-file-publish-seconds.txt") s |
| Baseline fixture conversion | $(cat "$artifact_root/baseline-conversion-seconds.txt") s |
| Single-file fixture conversion | $(cat "$artifact_root/single-file-conversion-seconds.txt") s |

The baseline and single-file executables produced byte-identical output for all fixtures.
EOF

cat "$artifact_root/size-report.md"
