FROM python:3.13.14-slim

RUN python -m pip install --no-cache-dir --upgrade pip \
    && python -m pip install --no-cache-dir graphifyy==0.9.11

WORKDIR /app/workspaces
ENTRYPOINT ["graphify"]
CMD ["--help"]
