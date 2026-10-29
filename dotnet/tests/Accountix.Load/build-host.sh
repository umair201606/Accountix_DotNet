#!/usr/bin/env bash
# Builds the web host (or a project that references it, e.g. tests/Accountix.Tests)
# with every module that compiles right now (others may be mid-edit), into
# $ART, and prints the module list it used (stdout).
#   ART=/path/to/artifacts tests/Accountix.Load/build-host.sh [Release|Debug] [project dir]
# A module that breaks between its own build and the host build is dropped
# and the build retried (up to 4 times). Module DLLs left in the output by an
# earlier build are deleted: the host loads every Accountix.*.dll it finds.
set -uo pipefail
cd "$(dirname "$0")/../.."
CONFIG="${1:-Release}"
PROJECT="${2:-src/Accountix.Web}"
ART="${ART:?set ART to your artifacts path}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
LOG="${TMPDIR:-/tmp}/ax_build_host.$$"
ALL="Accounting Finance FixedAssets Hr Inventory Invoicing Parties Payroll Platform Fbr SuperAdmin HrRecords Orders Returns Executive"
ok=()
for m in $ALL; do
  if dotnet build "src/Modules/Accountix.$m" -c "$CONFIG" --artifacts-path "$ART" -v q -nologo >"$LOG" 2>&1; then
    ok+=("$m")
  else
    echo "skip $m: $(grep -m1 ' error ' "$LOG" | sed 's/.* error /error /' | cut -c1-140)" >&2
  fi
done
OUT="$ART/bin/$(basename "$PROJECT")/$(echo "$CONFIG" | tr '[:upper:]' '[:lower:]')"
for attempt in 1 2 3 4; do
  list=$(IFS=+; echo "${ok[*]}")
  echo "build $PROJECT with: $list" >&2
  if dotnet build "$PROJECT" -c "$CONFIG" --artifacts-path "$ART" -p:AccountixModules="$list" -v q -nologo >"$LOG" 2>&1; then
    for m in $ALL; do
      echo "+$list+" | grep -q "+$m+" || rm -f "$OUT/Accountix.$m.dll" "$OUT/Accountix.$m.pdb"
    done
    rm -f "$LOG"; echo "$list"; exit 0
  fi
  broken=$(grep ' error ' "$LOG" | sed -nE 's#.*/src/Modules/Accountix\.([A-Za-z]+)/.*#\1#p' | sort -u)
  [ -z "$broken" ] && { cat "$LOG" >&2; exit 1; }
  echo "dropping: $broken" >&2
  keep=(); for m in "${ok[@]}"; do echo "$broken" | grep -qx "$m" || keep+=("$m"); done; ok=("${keep[@]}")
done
exit 1
