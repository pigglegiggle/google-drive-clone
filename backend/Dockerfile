FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY Drive.csproj packages.lock.json ./
RUN dotnet restore --locked-mode
COPY . .
RUN dotnet publish -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER root
RUN mkdir -p /app/keys /app/parts && chown -R app:app /app/keys /app/parts
USER app
ENV ASPNETCORE_HTTP_PORTS=8080 KEY_DIRECTORY=/app/keys TEMP_DIRECTORY=/app/parts
EXPOSE 8080
ENTRYPOINT ["dotnet", "Drive.dll"]
