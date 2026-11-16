FROM nginx:1.27-alpine

# Remove the default Nginx site.
RUN rm -rf /usr/share/nginx/html/*

# Use the SVAULT-specific Nginx configuration.
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Copy the static website into the Nginx web root.
COPY wwwroot/ /usr/share/nginx/html/

EXPOSE 80

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
  CMD wget -qO- http://127.0.0.1/healthz || exit 1

CMD ["nginx", "-g", "daemon off;"]
