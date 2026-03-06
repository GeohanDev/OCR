# Stage 1 — build
FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
# Copy PDF.js worker into public so it is served as a plain .js file (avoids nginx .mjs MIME-type issues)
RUN cp node_modules/pdfjs-dist/build/pdf.worker.min.mjs public/pdf.worker.min.js 2>/dev/null || \
    cp node_modules/pdfjs-dist/build/pdf.worker.min.js public/pdf.worker.min.js 2>/dev/null || \
    echo "WARNING: pdfjs worker not found"
ARG VITE_API_URL=
ARG VITE_ACUMATICA_URL
ARG VITE_ACUMATICA_CLIENT_ID
ENV VITE_API_URL=$VITE_API_URL
ENV VITE_ACUMATICA_URL=$VITE_ACUMATICA_URL
ENV VITE_ACUMATICA_CLIENT_ID=$VITE_ACUMATICA_CLIENT_ID

RUN npm run build

# Stage 2 — serve
FROM nginx:1.27-alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
CMD ["nginx", "-g", "daemon off;"]
