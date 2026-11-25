ARG LANDING_SERVER_IMAGE=ghcr.io/nginx/nginx-unprivileged:stable-alpine@sha256:637c35e46af7bc0dd3d3bcf7162e004e13ab3b370fde7cd416e2ac410b5a985a
FROM ${LANDING_SERVER_IMAGE}
ARG RELEASE_REVISION=unversioned
LABEL org.opencontainers.image.revision=${RELEASE_REVISION}

# Use the SVAULT-specific Nginx configuration.
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Copy the static website into the Nginx web root.
COPY wwwroot/ /usr/share/nginx/html/
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 CMD wget -q --spider http://127.0.0.1:8080/healthz || exit 1

CMD ["nginx", "-g", "daemon off;"]
