# GNU Make 3.81+ and Bash on macOS/Linux.
SHELL := /bin/bash
.DEFAULT_GOAL := help
ENV ?= local
PROJECT ?=
export ENV PROJECT

.PHONY: help setup build run stop logs migrate unit integration test
help:
	@printf '%s\n' \
	  'SETUP' \
	  '  make help                    Show this service interface' \
	  '  make setup                   Check Docker/Compose; preserve or create env files' \
	  '' 'BUILD / RUN' \
	  '  make build [ENV=local|dev]    Build the selected runnable image' \
	  '  make run [ENV=local|dev]      Build/start containers; wait for readiness' \
	  '  make stop [ENV=local|dev]     Stop selected project; preserve database data' \
	  '  make logs [ENV=local|dev]     Follow API logs (LOGS_ALL=1 includes dependencies)' \
	  '' 'DATABASE' \
	  '  make migrate [ENV=local|dev]  Explicit migrations using SDK container' \
	  '' 'TEST' \
	  '  make unit                    Unit tests in SDK container; no providers' \
	  '  make integration             Integration tests with isolated TEST providers' \
	  '  make test                    Complete unit + integration suite' \
	  '' 'ENV defaults to local. LOCAL: developer containers / Development.' \
	  'DEV: built containers / Staging. Host .NET is optional.' \
	  'Advanced operations and PROJECT overrides: docs/DEVELOPMENT.md.'

setup build run stop logs migrate:
	@bash scripts/develop.sh "$@" "$${ENV}" "$${PROJECT}"
unit integration test:
	@bash scripts/test.sh $(if $(filter test,$@),all,$@)
