# syntax=docker/dockerfile:1
#
# Serves the built Angular bundle. This is a STAND-IN for Azure Static Web Apps, used only
# because SWA cannot be created in the target subscription — see nginx.conf.template for the
# region details. The real host configuration is public/staticwebapp.config.json.

FROM node:22-alpine AS build
WORKDIR /src

COPY package.json package-lock.json ./
RUN npm ci

COPY . ./
# The production configuration swaps in environment.production.ts, which already carries the
# broker's hostname — stamped there by scripts/set-bff-url.mjs before this build runs.
RUN npx ng build --configuration production

FROM nginx:alpine AS runtime

# Anything the image ships by default would be served alongside the app.
RUN rm -rf /usr/share/nginx/html/* /etc/nginx/conf.d/default.conf

COPY --from=build /src/dist/quotes-web/browser /usr/share/nginx/html

# Files under /etc/nginx/templates are run through envsubst by the image's entrypoint at
# container start, which is how ${BFF_ORIGIN} reaches the Content-Security-Policy without
# baking the hostname into the image.
COPY nginx.conf.template /etc/nginx/templates/default.conf.template

EXPOSE 80
