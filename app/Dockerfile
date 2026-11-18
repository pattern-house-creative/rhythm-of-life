# The app: the built PWA plus its small Node server (server/node.ts).
#
#   docker build -t rhythm-app app
#
# Separate build and runtime dependency stages. The runtime image holds only the
# built files and the two runtime dependencies,
# runs as the image's non-root "node" user and writes nothing, so it runs with a read-only
# root filesystem (deploy/compose/app.compose.yaml). It listens on PORT (8080) on all
# interfaces inside the container; compose decides what reaches it from outside.
ARG NODE_IMAGE=node:24-alpine@sha256:116b800c3754b440ee0aa3ed2f20f5bf3d99a554bfd9adeea09ea8017f6e85f5

FROM ${NODE_IMAGE} AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . .
RUN npm run build

# Installing just runtime dependencies avoids pruning the large build tree, which can
# require expensive overlay filesystem renames on ZFS-backed Docker installations.
FROM ${NODE_IMAGE} AS runtime-deps
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --omit=dev --no-audit --no-fund

FROM ${NODE_IMAGE}
ENV NODE_ENV=production \
    HOST=0.0.0.0 \
    PORT=8080
WORKDIR /app
COPY --from=build /src/package.json ./
COPY --from=runtime-deps /src/node_modules ./node_modules
COPY --from=build /src/build ./build
COPY --from=build /src/dist ./dist
USER node
EXPOSE 8080
# /api/health answers without calling the engine, so a slow engine never marks the app unhealthy.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["node", "-e", "fetch('http://127.0.0.1:'+(process.env.PORT||8080)+'/api/health').then(r=>process.exit(r.ok?0:1),()=>process.exit(1))"]
CMD ["node", "build/server/node.js"]
