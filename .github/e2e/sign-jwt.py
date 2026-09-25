#!/usr/bin/env python3
"""Токен доступа для сквозного прогона — те же claims, что у TokenService.CreateAccessToken.

Ключ — только из окружения (E2E_SIGNING_KEY, base64, не короче 32 байт, как AuthOptions.SigningKeyBytes):
в командной строке он попал бы в журнал задачи. Только стандартная библиотека.
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import sys
import time


def b64url(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--sub", required=True, help="id игрока (uuid)")
    parser.add_argument("--issuer", default="gorodki")
    parser.add_argument("--audience", default="gorodki-app")
    # Срок — параметр CI, а не игры: хватает на сборку и тест.
    parser.add_argument("--lifetime", type=int, default=7200, help="секунды")
    args = parser.parse_args()

    raw = os.environ.get("E2E_SIGNING_KEY", "")
    try:
        key = base64.b64decode(raw, validate=True)
    except ValueError:
        print("E2E_SIGNING_KEY — не base64", file=sys.stderr)
        return 1
    if len(key) < 32:
        print("E2E_SIGNING_KEY короче 32 байт — сервер такой ключ не примет", file=sys.stderr)
        return 1

    now = int(time.time())
    header = {"alg": "HS256", "typ": "JWT"}
    payload = {
        "sub": args.sub,
        "role": "player",
        "iss": args.issuer,
        "aud": args.audience,
        "iat": now,
        "nbf": now,
        "exp": now + args.lifetime,
    }
    signing_input = b64url(json.dumps(header, separators=(",", ":")).encode()) + "." + b64url(
        json.dumps(payload, separators=(",", ":")).encode()
    )
    signature = hmac.new(key, signing_input.encode("ascii"), hashlib.sha256).digest()
    print(signing_input + "." + b64url(signature))
    return 0


if __name__ == "__main__":
    sys.exit(main())
