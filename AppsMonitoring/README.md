## AppsMonitoring

Tool to monitor and mitigate certain errors occurring in apps.
It's a single-replica stateful service that queries service owner telemetry and stores it in PostgreSQL.

```bash
dotnet test
```

### Log

To bootstrap env
```bash
cd infra
# For tt02 we use our prod account
az login --use-device-code
# Populates PG variables
. configure-env-shell.sh <key-vault-name>
# Initializes KV, seeds DB
./bootstrap-env.sh <key-vault-name>
```

Now we can deploy/promote to environment through GH.
