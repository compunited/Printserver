# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/nightly/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
COPY ./app/ ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "Printserver.dll"]

FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0 AS build
WORKDIR /src
COPY src/Printserver/ ./Printserver/
WORKDIR /src/Printserver
RUN dotnet restore
RUN dotnet publish -c Release -o /out

FROM runtime AS final
COPY --from=build /out/ /app/
