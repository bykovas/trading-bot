FROM nginx:1.27-alpine

# Per-host Open Graph substitution; see the file for why it cannot be done client-side.
COPY nginx/default.conf /etc/nginx/conf.d/default.conf
COPY public/ /usr/share/nginx/html/

EXPOSE 80
