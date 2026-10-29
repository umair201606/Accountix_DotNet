#!/usr/bin/env bash
# Restore the two speed-test databases from their dumps and point dotnet/tools/perf.json at them,
# so the [PerfFact] / [PerfLargeFact] tests run again (docs/SPEED_AND_CONCURRENCY.md).
#
#   dotnet/tools/restore_perf_db.sh                       # restore both, write perf.json
#   dotnet/tools/restore_perf_db.sh --replace             # drop and re-create databases that exist
#   dotnet/tools/restore_perf_db.sh --dumps DIR --host H --port P --user U
#
# The dumps (pg_dump -Fc of a Postgres 16 server, Alembic 0018_report_indexes, 29 Oct 2026):
#   accountix_ref_perf.dump        full year of Samba Private Limited (company 2), 2,760 sales invoices
#   accountix_ref_perf_large.dump  the same year at 4x (10,405 sales invoices, ~90k journal lines)
# kept in ~/Downloads/accountix_data_backup_2026-10-29/ (not in the repository: they are large).
#
# Needs psql, createdb, dropdb and pg_restore of Postgres 16 or newer (PATH, or PG_BIN=<dir>) and a
# server of 16 or newer. Connection: the flags, else PGHOST / PGPORT / PGUSER (PGPASSWORD for a
# password; it is written into perf.json's connection strings, which stay on this machine).
# The [PerfFact] tests also need tools/reference.json (the reference database), next to perf.json.
#
# Options:
#   --dumps DIR        folder holding the two .dump files
#   --host H           server host or socket folder (default $PGHOST, else localhost)
#   --port P           (default $PGPORT, else 5432)
#   --user U           (default $PGUSER, else postgres)
#   --name DB          database for the full year (default accountix_ref_perf)
#   --large-name DB    database for the 4x year (default accountix_ref_perf_large)
#   --only-small       restore the full year only (perf.json then has no "large" section)
#   --out FILE         perf.json to write (default: perf.json next to this script)
#   --replace          drop a database of that name first (it refuses otherwise)
#   --jobs N           parallel pg_restore jobs (default 4)
set -euo pipefail

DUMPS="${HOME}/Downloads/accountix_data_backup_2026-10-29"
HOST="${PGHOST:-localhost}"
PORT="${PGPORT:-5432}"
USER_="${PGUSER:-postgres}"
SMALL_DB="accountix_ref_perf"
LARGE_DB="accountix_ref_perf_large"
OUT="$(cd "$(dirname "$0")" && pwd)/perf.json"
REPLACE=0
ONLY_SMALL=0
JOBS=4

die() { echo "restore_perf_db: $*" >&2; exit 1; }

while [ $# -gt 0 ]; do
  case "$1" in
    --dumps) DUMPS="$2"; shift 2 ;;
    --host) HOST="$2"; shift 2 ;;
    --port) PORT="$2"; shift 2 ;;
    --user) USER_="$2"; shift 2 ;;
    --name) SMALL_DB="$2"; shift 2 ;;
    --large-name) LARGE_DB="$2"; shift 2 ;;
    --only-small) ONLY_SMALL=1; shift ;;
    --out) OUT="$2"; shift 2 ;;
    --replace) REPLACE=1; shift ;;
    --jobs) JOBS="$2"; shift 2 ;;
    -h|--help) sed -n '2,31p' "$0"; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
done

tool() {
  if [ -n "${PG_BIN:-}" ]; then echo "${PG_BIN%/}/$1"; else command -v "$1" || die "$1 not found: put Postgres 16+ client tools on PATH or set PG_BIN"; fi
}
PSQL="$(tool psql)"; PG_RESTORE="$(tool pg_restore)"; CREATEDB="$(tool createdb)"; DROPDB="$(tool dropdb)"
CONN=(-h "$HOST" -p "$PORT" -U "$USER_")

major="$("$PG_RESTORE" --version | sed -E 's/[^0-9]*([0-9]+).*/\1/')"
[ "$major" -ge 16 ] || die "pg_restore $major is older than the dumps (made by pg_dump 16)"
"$PSQL" "${CONN[@]}" -d postgres -Atqc "select 1" >/dev/null || die "cannot reach Postgres at $HOST:$PORT as $USER_"

q() { "$PSQL" "${CONN[@]}" -d "$1" -v ON_ERROR_STOP=1 -Atqc "$2"; }

restore() {  # restore <dump file> <database>
  local dump="$1" db="$2"
  [ -f "$dump" ] || die "missing $dump"
  if [ "$(q postgres "select count(*) from pg_database where datname = '$db'")" = 1 ]; then
    [ "$REPLACE" = 1 ] || die "database $db exists; re-run with --replace to drop and re-create it"
    echo "dropping $db"
    "$DROPDB" "${CONN[@]}" --force "$db"
  fi
  echo "restoring $(basename "$dump") into $db"
  "$CREATEDB" "${CONN[@]}" "$db"
  "$PG_RESTORE" "${CONN[@]}" -d "$db" --no-owner --no-privileges --exit-on-error -j "$JOBS" "$dump"
  local rev; rev="$(q "$db" "select version_num from alembic_version")"
  [ "$rev" = "0018_report_indexes" ] || echo "note: $db is at Alembic $rev, not 0018_report_indexes" >&2
}

json_str() { printf '"%s"' "$(printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g')"; }

TABLES=(inv_invoices inv_invoice_items inv_purchase_invoices inv_sales_returns inv_purchase_returns
        inv_sales_orders inv_purchase_orders accounting_vouchers accounting_voucher_lines journal_entries
        journal_lines gl_daily_balances stock_ledger stock_layers payment_allocations audit_logs
        inv_customers inv_suppliers inv_products)

facts() {  # facts <database> <indent>: the JSON members perf.json holds for one database
  local db="$1" pad="$2" cid admin month count ystart yend cs url
  # The seeded company (Samba, slug "samba"); else the one with the most sales invoices.
  cid="$(q "$db" "select coalesce((select id from companies where slug = 'samba'),
                   (select company_id from inv_invoices group by company_id order by count(*) desc limit 1))")"
  [ -n "$cid" ] || die "$db has no company with sales invoices"
  admin="$(q "$db" "select id from users where is_super_admin order by id limit 1")"
  IFS='|' read -r month count <<<"$(q "$db" "select to_char(invoice_date, 'YYYY-MM'), count(*) from inv_invoices
                   where company_id = $cid group by 1 order by 2 desc, 1 limit 1")"
  IFS='|' read -r ystart yend <<<"$(q "$db" "select start_date, end_date from accounting_periods where company_id = $cid
                   and start_date <= date '$month-01' and end_date >= date '$month-01' order by start_date limit 1")"
  cs="Host=$HOST;Port=$PORT;Database=$db;Username=$USER_"
  if [ -n "${PGPASSWORD:-}" ]; then cs="$cs;Password=$PGPASSWORD"; fi
  case "$HOST" in
    /*) url="postgresql://$USER_:${PGPASSWORD:-}@/$db?host=$HOST&port=$PORT" ;;
    *)  url="postgresql://$USER_:${PGPASSWORD:-}@$HOST:$PORT/$db" ;;
  esac
  printf '%s"connection_string": %s,\n' "$pad" "$(json_str "$cs")"
  printf '%s"database_url": %s,\n' "$pad" "$(json_str "$url")"
  printf '%s"company_id": %s,\n%s"admin_user_id": %s,\n' "$pad" "$cid" "$pad" "$admin"
  printf '%s"busiest_month": "%s",\n%s"busiest_month_sales_invoices": %s,\n' "$pad" "$month" "$pad" "$count"
  printf '%s"year_start": "%s",\n%s"year_end": "%s",\n' "$pad" "$ystart" "$pad" "$yend"
  printf '%s"rows": {\n' "$pad"
  local i n last=$((${#TABLES[@]} - 1))
  for i in "${!TABLES[@]}"; do
    n="$(q "$db" "select count(*) from ${TABLES[$i]} where company_id = $cid")"
    printf '%s  "%s": %s%s\n' "$pad" "${TABLES[$i]}" "$n" "$([ "$i" = "$last" ] && echo "" || echo ",")"
  done
  printf '%s}' "$pad"
}

restore "$DUMPS/accountix_ref_perf.dump" "$SMALL_DB"
[ "$ONLY_SMALL" = 1 ] || restore "$DUMPS/accountix_ref_perf_large.dump" "$LARGE_DB"

tmp="$(mktemp "${OUT}.XXXXXX")"
{
  echo "{"
  facts "$SMALL_DB" "  "
  if [ "$ONLY_SMALL" = 1 ]; then
    echo ""
  else
    echo ","
    echo '  "large": {'
    facts "$LARGE_DB" "    "
    echo ""
    echo "  }"
  fi
  echo "}"
} >"$tmp"
mv "$tmp" "$OUT"
echo "wrote $OUT"
