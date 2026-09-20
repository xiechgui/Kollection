FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Collection.Web/Collection.Web.csproj src/Collection.Web/
RUN dotnet restore src/Collection.Web/Collection.Web.csproj
COPY src/Collection.Web/ src/Collection.Web/
RUN dotnet publish src/Collection.Web/Collection.Web.csproj -c Release --no-restore --self-contained false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out/ ./
ENV COLLECTION_CLOUD=1 COLLECTION_DATA=/data PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Collection.Web.dll"]
