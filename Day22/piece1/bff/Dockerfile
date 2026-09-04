# syntax=docker/dockerfile:1

FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json* ./
# `npm ci` needs a lockfile; fall back to `npm install` so a fresh clone builds before the
# lockfile has been generated and committed.
RUN if [ -f package-lock.json ]; then npm ci --omit=dev; else npm install --omit=dev; fi

FROM node:22-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production

COPY --from=deps /app/node_modules ./node_modules
COPY package.json ./
COPY src ./src

# Runs unprivileged. The only credential in this container is fetched at runtime from the
# platform's identity endpoint, so root buys nothing and costs the usual blast radius.
USER node

EXPOSE 8080
CMD ["node", "src/server.js"]
