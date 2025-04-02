#!/bin/bash

# Just to initialize some variables in we expect in Azure KV
# Prereqs:
# * Azure CLI
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
