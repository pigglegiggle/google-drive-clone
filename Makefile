.PHONY: configure up down logs test browser-test build format
configure:
	python3 scripts/configure.py
up:
	docker compose up -d --build --wait
down:
	docker compose down
logs:
	docker compose logs -f --tail=50
test:
	BASE_URL=http://localhost:8090 python3 tests/integration.py
browser-test:
	UI_URL=http://localhost:8090 npm run test:e2e --prefix frontend
build:
	dotnet build backend/Drive.csproj
	npm ci --prefix frontend
	npm run build --prefix frontend
format:
	dotnet format backend/Drive.csproj
	npm run format --prefix frontend
