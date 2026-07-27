# Deploying SupportPilot to Azure Container Apps

This is a **runbook you run yourself** — it needs an Azure account with billing
and an `az login`, which this project's tooling deliberately does not automate.
The `docker-compose.yml` stack is the free, local, one-command path; this doc is
for when you want a live URL.

## The one decision you must make first: where does the LLM live?

SupportPilot's generation + embeddings come from **Ollama running locally on your
GPU**. A container in Azure **cannot reach your laptop's Ollama.** So before you
deploy anything, pick how the cloud app will get an LLM:

| Option | What changes | Cost |
|---|---|---|
| **A. Azure OpenAI** | Point `Ollama__BaseUrl` at an Azure OpenAI endpoint (its API differs from Ollama's, so the `EmbeddingClient` / chat call need adapting — the "swappable adapter" idea in the revision notes). | Pay per token (small for a demo). |
| **B. Ollama on a cloud GPU** | Run Ollama in its own container on a GPU-enabled Azure VM / Container App and point `Ollama__BaseUrl` at it. Keeps the code identical. | GPU compute is **not** free. |
| **C. Don't deploy the LLM** | Deploy only Qdrant + backend + frontend, and keep generation local via a tunnel (e.g. expose local Ollama). Fine for a demo, not for a real service. | Free, fragile. |

Everything below assumes you've chosen one and have an Ollama-compatible endpoint
URL to set as `Ollama__BaseUrl`.

## Prerequisites (you run these)

```bash
az login                       # opens a browser; authenticate yourself
az account set --subscription "<your-subscription>"
az extension add --name containerapp --upgrade
```

## 1. Create a resource group + Azure Container Registry (ACR)

```bash
az group create --name supportpilot-rg --location eastus

az acr create --resource-group supportpilot-rg \
  --name <youracrname> --sku Basic --admin-enabled true
az acr login --name <youracrname>
```

## 2. Build and push the two images

The Dockerfiles are the same ones `docker-compose.yml` uses.

```bash
ACR=<youracrname>.azurecr.io

docker build -t $ACR/supportpilot-backend:latest  ./backend/SupportPilot.Api
docker build -t $ACR/supportpilot-frontend:latest ./frontend
docker push $ACR/supportpilot-backend:latest
docker push $ACR/supportpilot-frontend:latest
```

## 3. Create the Container Apps environment

```bash
az containerapp env create --name supportpilot-env \
  --resource-group supportpilot-rg --location eastus
```

## 4. Deploy Qdrant, the backend, and the frontend

Qdrant (internal only). For a real deployment give it a persistent volume; a
plain container is fine for a demo but loses vectors on restart.

```bash
az containerapp create --name qdrant \
  --resource-group supportpilot-rg --environment supportpilot-env \
  --image qdrant/qdrant --target-port 6333 --ingress internal
```

Backend (internal ingress; the frontend talks to it). Set the two URLs — Qdrant
by its internal app name, Ollama to whatever you chose in the decision above.

```bash
az containerapp create --name backend \
  --resource-group supportpilot-rg --environment supportpilot-env \
  --image $ACR/supportpilot-backend:latest \
  --registry-server $ACR \
  --target-port 8080 --ingress internal \
  --env-vars \
    "Qdrant__BaseUrl=http://qdrant" \
    "Ollama__BaseUrl=<your-llm-endpoint>"
```

Frontend (external ingress = your public URL). The baked-in nginx proxies
`/chat|/ingest|/search` to `http://backend:8080`; on Container Apps the backend
is reachable by its app name, so update `frontend/nginx.conf`'s `proxy_pass` to
`http://backend` (port 80 of the internal ingress) before building the image, or
front both with a single app. Then:

```bash
az containerapp create --name frontend \
  --resource-group supportpilot-rg --environment supportpilot-env \
  --image $ACR/supportpilot-frontend:latest \
  --registry-server $ACR \
  --target-port 80 --ingress external
```

`az containerapp show --name frontend -g supportpilot-rg --query properties.configuration.ingress.fqdn -o tsv`
prints the public URL.

## 5. Seed the docs

Point the seed script at the backend (or run an ingest from inside the network):

```bash
pwsh ./scripts/seed-docs.ps1 -ApiBase https://<backend-fqdn> -QdrantBase <qdrant-internal>
```

## Automating it later (CI/CD)

The CI workflow (`.github/workflows/ci.yml`) currently **builds** the images on
every push but does not deploy. To make it deploy, add a `deploy` job gated on
`main` that: authenticates with `azure/login@v2` (using an
`AZURE_CREDENTIALS` repo secret you create), pushes the images to ACR, and runs
`az containerapp update`. That secret and the Azure account are yours to set up —
which is exactly why this step is a documented runbook rather than an automated
part of the build.
