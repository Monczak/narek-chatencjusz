# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

COPY services/brain/BrainService.csproj services/brain/
RUN dotnet restore "services/brain/BrainService.csproj"

COPY shared/proto /proto/

COPY services/brain/ ./services/brain/

WORKDIR /src/services/brain
RUN dotnet publish -c Release -o /app/publish -p:ProtoBaseDir=/proto

# Run stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
EXPOSE 5050

ENTRYPOINT ["dotnet", "BrainService.dll"]
