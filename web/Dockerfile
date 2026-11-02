# syntax=docker/dockerfile:1
# Image of one web app for docker-compose.yml (docs/09 "With Docker Compose"): the production build (`vite build`,
# OIDC against the realm of .env.production) served by an unprivileged nginx on port 8080, which proxies /api to the
# API on the same origin and sends the security headers of web/nginx.conf.template. Build context: the repository
# root; APP is the folder under web/apps (customer-portal or admin-console).
#
#   docker build -f web/Dockerfile --build-arg APP=customer-portal .
#
# Layer order keeps rebuilds short: the install depends only on the package manifests and the lockfile (and installs
# both apps, so the portal and admin images share that layer), a source change starts at the build, and only the
# sources of this app and the shared packages are copied, so a change in the other app does not rebuild this image.
# The base images are pinned to patch releases.

ARG NODE_VERSION=22.23.4
ARG NGINX_VERSION=1.29.8

FROM --platform=$BUILDPLATFORM node:${NODE_VERSION}-bookworm-slim AS build
ENV COREPACK_ENABLE_DOWNLOAD_PROMPT=0 \
    PNPM_HOME=/pnpm \
    CI=true
WORKDIR /web
# The workspace's manifests only (every package of pnpm-workspace.yaml, so the frozen lockfile matches the workspace).
COPY web/package.json web/pnpm-lock.yaml web/pnpm-workspace.yaml ./
COPY --parents web/apps/*/package.json web/packages/*/package.json web/e2e/package.json /
# corepack installs the pnpm version web/package.json pins. The optional build secret `extra_ca_certs` (PEM) is
# trusted for these downloads when building behind a TLS-inspecting proxy (docs/09); without it nothing changes.
RUN --mount=type=secret,id=extra_ca_certs,required=false \
    --mount=type=cache,id=docproc-pnpm-store,target=/pnpm/store \
    if [ -s /run/secrets/extra_ca_certs ]; then export NODE_EXTRA_CA_CERTS=/run/secrets/extra_ca_certs; fi; \
    corepack enable \
    && pnpm config set store-dir /pnpm/store \
    && pnpm install --frozen-lockfile --filter "@docproc/customer-portal..." --filter "@docproc/admin-console..."
# APP is the folder under web/apps, which is also the package name without the @docproc/ scope. Declared after the
# install, which therefore does not depend on it.
ARG APP
RUN test -n "$APP" -a -f "apps/$APP/package.json" || { echo "APP must name a folder under web/apps (got '$APP')" >&2; exit 1; }
COPY web/tsconfig.base.json ./
COPY web/packages/ packages/
COPY web/apps/${APP}/ apps/${APP}/
# VITE_OIDC_AUTHORITY overrides the app's .env.production (Vite prefers the environment) when it is set, which is how
# docker-compose.yml moves Keycloak to DOCPROC_KEYCLOAK_PORT; empty or unset, .env.production applies.
ARG VITE_OIDC_AUTHORITY
RUN if [ -z "$VITE_OIDC_AUTHORITY" ]; then unset VITE_OIDC_AUTHORITY; fi; \
    pnpm --filter "@docproc/$APP" build

FROM nginxinc/nginx-unprivileged:${NGINX_VERSION}-alpine AS serve
ARG APP
# The image renders /etc/nginx/templates/*.template with the environment at start-up (API_UPSTREAM, OIDC_ORIGIN) and
# runs nginx as the user nginx (uid 101), which cannot bind ports below 1024, hence 8080.
COPY web/nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /web/apps/${APP}/dist /usr/share/nginx/html
# OIDC_ORIGIN: the origin of the app's OIDC authority (.env.production here; docker-compose.yml sets it with
# VITE_OIDC_AUTHORITY, ComposeParityTests checks both), which the Content-Security-Policy lets the app call (discovery,
# token) and frame (silent renew).
ENV API_UPSTREAM=http://api:8080 \
    OIDC_ORIGIN=http://localhost:8080
EXPOSE 8080
