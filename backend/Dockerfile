# syntax=docker/dockerfile:1
# Образ сервера «Городков» для Render. Контекст сборки — папка backend/:
#   docker build -t gorodki-api backend

# 1. Сборка на полном SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только файлы проектов: пока они не меняются, Docker берёт restore из кэша.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Gorodki.Domain/Gorodki.Domain.csproj src/Gorodki.Domain/
COPY src/Gorodki.Api/Gorodki.Api.csproj src/Gorodki.Api/
RUN dotnet restore src/Gorodki.Api/Gorodki.Api.csproj

COPY src/ src/
RUN dotnet publish src/Gorodki.Api/Gorodki.Api.csproj --configuration Release --no-restore --output /app

# 2. Запуск на минимальном образе без shell и пакетного менеджера.
#    Вариант «extra» содержит tzdata и ICU — они нужны для времени Минска.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .

# Порт по умолчанию — 8080; Render передаёт свой порт в переменной PORT (см. Program.cs).
EXPOSE 8080
ENTRYPOINT ["dotnet", "Gorodki.Api.dll"]
