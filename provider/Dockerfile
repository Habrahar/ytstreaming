FROM node:22-bookworm-slim AS javascript
FROM python:3.13-slim-bookworm
COPY --from=javascript /usr/local/bin/node /usr/local/bin/node
RUN apt-get update && apt-get install -y --no-install-recommends libstdc++6 && rm -rf /var/lib/apt/lists/*
WORKDIR /provider
COPY provider/requirements.txt .
RUN pip install --no-cache-dir --disable-pip-version-check -r requirements.txt
COPY provider/server.py provider/extract.py ./
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1
USER 1654
EXPOSE 8081
CMD ["python", "server.py"]
