# SupportPilot frontend — multi-stage build.
#
# Stage 1 (build): Node builds the Vite app into a folder of static files.
# Stage 2 (final): nginx serves those static files AND reverse-proxies the API
# paths (/chat, /ingest, /search) to the backend container — so the browser sees
# a single origin and there's no CORS, exactly like the Vite dev proxy did in dev.

# --- build ---
FROM node:22-alpine AS build
WORKDIR /app

# Restore deps first (cache layer) from the lockfile, then build.
COPY package*.json ./
RUN npm ci
COPY . ./
RUN npm run build

# --- final ---
FROM nginx:alpine AS final
# Our server block replaces the default site (static files + API proxy).
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
