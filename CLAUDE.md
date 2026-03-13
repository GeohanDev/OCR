# OCR System — Claude Instructions

## Build Rule
After making **any code modification**, always run both commands from the project root:
```
docker compose build
docker compose up -d --no-build
```
Do not skip either step. `build` alone does not apply the changes — `up -d` is required to restart the containers with the new images.
