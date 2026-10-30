FROM alpine:3.23
RUN apk add --no-cache openssh-client && adduser -D -u 1000 tunnel
USER tunnel
ENTRYPOINT ["ssh"]
