#!/bin/bash

# Just to initialize some variables in we expect in Azure KV
# Prereqs:
# * Azure CLI
# * Export PGxxxx variables for psql to connect to corresponding db (see Settings -> Connect in Azure portal)
#   * Creds can be found in KV
# Ex: ./bootstrap-env.sh <key-vault-name>

set -e

echo "Vault: $1"
valid_environments=("at24" "tt02" "prod")

altinnenv=$(echo "$1" | awk -F'-' '{print $3}')

if [[ " ${valid_environments[@]} " =~ " $altinnenv " ]]; then
    echo "Valid environment: $altinnenv"
else
    echo "Invalid environment: $altinnenv"
    exit 1
fi
az keyvault secret set --vault-name "$1" --name AppConfiguration--DisableSlackAlerts --value true
az keyvault secret set --vault-name "$1" --name AppConfiguration--AltinnEnvironment --value "$altinnenv"

psql << EOF
CREATE TABLE IF NOT EXISTS seed(
    id  integer PRIMARY KEY GENERATED BY DEFAULT AS IDENTITY,
    data bytea NOT NULL
);
EOF

psql << EOF
\lo_import 'data.db'
SELECT :LASTOID AS lo_oid \gset
INSERT INTO seed (data) VALUES (lo_get(:lo_oid));
SELECT lo_unlink(:lo_oid);
EOF
