# Stage 1 — build
FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
ARG VITE_API_URL=/api
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
