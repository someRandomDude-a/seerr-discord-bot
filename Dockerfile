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

COPY ./seerr /app
COPY bot.py /app

CMD ["python", "bot.py"]