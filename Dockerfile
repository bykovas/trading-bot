FROM python:3.12-alpine AS archive
WORKDIR /build
COPY public/zurnalas.html public/zurnalas.html
COPY nginx/build-archive.py nginx/build-archive.py
COPY nginx/test_archive.py nginx/test_archive.py
RUN python3 -m unittest discover -s nginx -v && python3 nginx/build-archive.py public/zurnalas.html /archive.html

FROM nginx:1.27-alpine

# Per-host Open Graph substitution; see the file for why it cannot be done client-side.
COPY nginx/default.conf /etc/nginx/conf.d/default.conf
COPY public/ /usr/share/nginx/html/

EXPOSE 80

COPY --from=archive /archive.html /usr/share/nginx/archive/index.html
