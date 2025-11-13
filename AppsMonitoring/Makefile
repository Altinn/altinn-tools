
.PHONY: build
build:
	dotnet build

.PHONY: test
test:
	dotnet test --logger "console;verbosity=detailed"

build-image:
	docker build -t altinncr.azurecr.io/apps-monitor/image:latest -f src/Altinn.Apps.Monitoring/Dockerfile .

run-image: build-image
	docker run --rm altinncr.azurecr.io/apps-monitor/image:latest

scan-image:
	trivy image altinncr.azurecr.io/apps-monitor/image:latest

dive-image:
	dive altinncr.azurecr.io/apps-monitor/image:latest

acr-login:
	az acr login --name altinncr

push-image: build-image
	docker push altinncr.azurecr.io/apps-monitor/image:latest

build-manifests:
	kubectl kustomize infra/deployment/at24 > infra/deployment/at24/result.yaml

debug-container:
	$(eval POD := $(shell kubectl get po -o name --no-headers=true -l app=apps-monitor -n apps-monitor))
	KUBECTL_DEBUG_CUSTOM_PROFILE=true kubectl debug --custom infra/debug-profile.json $(POD) -n apps-monitor -it --image=nicolaka/netshoot
