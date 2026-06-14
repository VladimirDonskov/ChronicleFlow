FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/ChronicleFlow.Api/ChronicleFlow.Api.csproj \
    --configuration Release \
    --output /app \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
VOLUME ["/app/data"]
ENTRYPOINT ["dotnet", "ChronicleFlow.Api.dll"]
