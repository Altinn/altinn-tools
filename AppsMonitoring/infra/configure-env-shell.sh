#!/bin/bash

# Used to initialize the shell with PG variables
# Prereqs:
# * Azure CLI - logged in using normal account for at24, prod account for tt02 & prod
# Ex: . configure-env-shell.sh <key-vault-name>

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

host=$(az keyvault secret show --vault-name "$1" --name AppConfiguration--DbAdmin--Host --query "value" -o tsv)
user=$(az keyvault secret show --vault-name "$1" --name AppConfiguration--DbAdmin--Username --query "value" -o tsv)
db=$(az keyvault secret show --vault-name "$1" --name AppConfiguration--DbAdmin--Database --query "value" -o tsv)
pw=$(az keyvault secret show --vault-name "$1" --name AppConfiguration--DbAdmin--Password --query "value" -o tsv)

export PGHOST="$host"
export PGUSER="$user"
export PGPORT=5432
export PGDATABASE="$db"
export PGPASSWORD="$pw"
