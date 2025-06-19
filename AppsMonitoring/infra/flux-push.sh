#!/bin/bash

# Runs flux push
# Needs to be able to log into ACR:
#   az acr login --name altinncr
# ./flus-push.sh

f() {
    # Function is executed in subshell to avoid changing working directory
    cd deployment/

    flux push artifact oci://altinncr.azurecr.io/apps-monitor/configs:$(git rev-parse --short HEAD) \
        --provider=generic \
        --reproducible \
        --path="." \
        --source="$(git config --get remote.origin.url)" \
        --revision="$(git branch --show-current)/$(git rev-parse HEAD)"
    flux tag artifact oci://altinncr.azurecr.io/apps-monitor/configs:$(git rev-parse --short HEAD) \
        --provider=generic \
        --tag at24

    # The source is created in Terraform, we just refer to it here
    flux reconcile source oci -n apps-monitor apps-monitor
}

(set -e; f)
