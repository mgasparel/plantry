#!/bin/bash
set -e

ROLE_EXISTS=$(psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -tAc \
    "SELECT 1 FROM pg_roles WHERE rolname='app_user'")

if [ "$ROLE_EXISTS" != "1" ]; then
    psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" \
        -c "CREATE ROLE app_user LOGIN NOSUPERUSER NOBYPASSRLS"
    psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" \
        -v pw="$APP_USER_PASSWORD" <<'EOSQL'
ALTER ROLE app_user PASSWORD :'pw';
EOSQL
fi
