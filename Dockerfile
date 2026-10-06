FROM node:22-slim AS activity-build
WORKDIR /activity
COPY activity/package*.json ./
RUN npm ci
COPY activity/ ./
RUN npm run build

FROM python:3.14-slim

# Set environment variables
ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1
ENV DATABASE_PATH=seerr_cache.db
ENV DATA_DIR=/data

WORKDIR /app

# Install Python dependencies
COPY requirements.txt /app
RUN pip install --no-cache-dir -r requirements.txt

COPY ./seerr /app/seerr
COPY ./media_bot /app/media_bot
COPY bot.py /app
COPY --from=activity-build /activity/dist /app/activity/dist

RUN useradd --create-home --uid 10001 mediahub && mkdir -p /data && chown mediahub:mediahub /data
USER mediahub
EXPOSE 8080

CMD ["python", "bot.py"]
