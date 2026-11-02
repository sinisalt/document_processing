# syntax=docker/dockerfile:1
# Image of one web app for docker-compose.yml (docs/09 "With Docker Compose"): the production build (`vite build`,
# OIDC against the realm of .env.production) served by nginx, which proxies /api to the API on the same origin.
# Build context: the repository root; APP is the folder under web/apps (customer-portal or admin-console).
#
#   docker build -f web/Dockerfile --build-arg APP=customer-portal .

ARG NODE_VERSION=22
ARG NGINX_VERSION=1.29

FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-bookworm-slim AS build
# APP is the folder under web/apps, which is also the package name without the @docproc/ scope.
ARG APP
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0 \
    PNPM_HOME=/pnpm \
    CI=true
WORKDIR /web
COPY web/ ./
# corepack installs the pnpm version web/package.json pins. The optional build secret `extra_ca_certs` (PEM) is
# trusted for these downloads when building behind a TLS-inspecting proxy (docs/09); without it nothing changes.
RUN --mount=type=secret,id=extra_ca_certs,required=false \
    --mount=type=cache,id=docproc-pnpm-store,target=/pnpm/store \
    test -n "$APP" -a -d "apps/$APP" || { echo "APP must name a folder under web/apps (got '$APP')" >&2; exit 1; }; \
    if [ -s /run/secrets/extra_ca_certs ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/extra_ca_certs; fi; \
    corepack enable \
    && pnpm config set store-dir /pnpm/store \
    && pnpm install --frozen-lockfile --filter "@docproc/$APP..." \
    && pnpm --filter "@docproc/$APP" build

FROM nginx:${NGINX_VERSION}-alpine AS serve
ARG APP
# The official image renders /etc/nginx/templates/*.template with the environment at start-up (API_UPSTREAM).
COPY web/nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /web/apps/${APP}/dist /usr/share/nginx/html
ENV API_UPSTREAM=http://api:8080
EXPOSE 80
