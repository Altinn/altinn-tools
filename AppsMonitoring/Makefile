
build:
	dotnet build

.PHONY: test
test:
	dotnet test --logger "console;verbosity=detailed"

build-image:
	docker build -t altinntjenesterregistry.azurecr.io/altinn-apps-monitor:latest -f src/Altinn.Apps.Monitoring/Dockerfile .

run-image: build-image
	docker run --rm altinntjenesterregistry.azurecr.io/altinn-apps-monitor:latest

scan-image:
	trivy image altinntjenesterregistry.azurecr.io/altinn-apps-monitor:latest

dive-image:
	dive altinntjenesterregistry.azurecr.io/altinn-apps-monitor:latest

acr-login:
	az acr login --name altinntjenesterregistry

push-image: build-image
	docker push altinntjenesterregistry.azurecr.io/altinn-apps-monitor:latest

build-manifests: push-image
	kubectl kustomize infra/deployment/at24 > infra/deployment/at24/result.yaml

deploy-manifests: build-manifests
	kubectl apply -k infra/deployment/at24
