# Servidor puente de TecLink: para permanencia sin depender de ninguna PC (Railway u otro host con Docker).
# Construir con el contexto en la raiz del repositorio:  docker build -f src/TecLink.Relay/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TecLink.Relay/ src/TecLink.Relay/
RUN dotnet publish src/TecLink.Relay -c Release -o /out
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /out .
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TecLink.Relay.dll"]
