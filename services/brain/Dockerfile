# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

USER root
RUN apt-get update && apt-get install -y libgomp1 && rm -rf /var/lib/apt/lists/*
USER $APP_UID

WORKDIR /src

COPY services/brain/BrainService.csproj services/brain/
RUN dotnet restore "services/brain/BrainService.csproj"

COPY shared/proto /proto/

COPY services/brain/ ./services/brain/

WORKDIR /src/services/brain
RUN dotnet publish -c Release -o /app/publish -p:ProtoBaseDir=/proto

# Run stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
ENV ASPNETCORE_HTTP_PORTS=""

WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080
EXPOSE 5050

ENTRYPOINT ["dotnet", "BrainService.dll"]
