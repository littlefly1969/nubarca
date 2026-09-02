# Multi-stage build:
#   1. node:22-alpine — install deps + run `npm run build` to produce dist/.
#      Node 22 matches the repository's canonical Node major (`.nvmrc`).
#   2. nginx:alpine — copy dist/ into the standard nginx web root + a tiny
#      config that adds SPA fallback (rewrite anything not on disk to
#      /index.html so client-side routes survive a hard reload).
#
# The API + share endpoints are NOT proxied from inside this container — the
# host's reverse proxy is responsible for routing `/api/*`, `/health`, and
# `/s/*` to the api container. Keeping that split outside this image means
# the same frontend bundle works under nginx, Caddy, Traefik, etc. without
# being aware of the upstream API.

FROM node:22-alpine AS build

# The build context is the repository root, rather than frontend/ alone.
# @nubarca/contracts is source-shared with the mobile and TV clients and the
# frontend TypeScript project references it through ../packages/contracts.
WORKDIR /app/frontend

COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci

COPY frontend ./
COPY packages/contracts ../packages/contracts
RUN npm run build

FROM nginx:alpine AS runtime
COPY --from=build /app/frontend/dist /usr/share/nginx/html
COPY frontend/nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80
