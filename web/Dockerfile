# Build context: web/ (see docker-compose.yml).
# Base images are pinned by digest (tags are mutable); the tag is kept for readability and is ignored by Docker.
# Angular 22 needs Node ^22.22.3 or ^24.15.0; CI uses the same Node version.
FROM node:24.22.1-alpine@sha256:06d7adf3e0ef61a13f1d28682726366f97a15aa1888c934d3ecec86c33918b53 AS build
WORKDIR /src

# Install first so the package layer is cached until the lock file changes.
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .
RUN npx ng build

FROM nginx:1.30.6-alpine@sha256:901e593c95dc85f5a729f5765516321366ef4ac401821431d408897e95287d0a AS runtime
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/fieldtrack-web/browser /usr/share/nginx/html
