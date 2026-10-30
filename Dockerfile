# syntax=docker/dockerfile:1.7
#
# Small image: a self-contained, trimmed build on the chiseled (distroless) runtime-deps base, about 18.5 MB compressed.
# For comparison, the usual aspnet base plus the app is about 97 MB. The operator is a plain worker host (no ASP.NET Core), which is
# what keeps the app payload to about 13 MB compressed.
#
#   docker build -t eks-devops-operator .
#   docker buildx build --platform linux/amd64,linux/arm64 -t <registry>/eks-devops-operator --push .

# The build stage always runs on the build machine's own architecture and cross-publishes for the target,
# so multi-arch builds (including arm64 / Graviton) need no emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore first, on its own layer: it is only redone when the project file changes. No cache mount: it would not travel with the layer cache, so a cached restore step would leave publish without packages.
# These properties must match between restore and publish, or --no-restore cannot find the runtime and trimmer packs.
ENV PUBLISH_PROPS="-p:SelfContained=true -p:PublishTrimmed=true -p:TrimMode=partial -p:UseAppHost=true -p:DebugType=none -p:DebugSymbols=false"
COPY global.json ./
COPY src/EksDevopsOperator/EksDevopsOperator.csproj src/EksDevopsOperator/
RUN \
    RID="linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)" && \
    dotnet restore src/EksDevopsOperator -r "$RID" $PUBLISH_PROPS

# TrimMode=partial trims only the .NET framework. KubeOps, the AWS SDK and the Kubernetes client stay whole, because they use
# reflection (JSON models, DI, request marshalling) that the trimmer cannot see.
# The rm line drops debugger and crash-dump support, the standalone GC builds and the LTTng trace provider (about 7 MB together).
# None is used unless a DOTNET_* variable asks for it, and diagnostics are switched off below.
COPY src src
RUN \
    RID="linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)" && \
    dotnet publish src/EksDevopsOperator -c Release -r "$RID" --no-restore -o /out $PUBLISH_PROPS && \
    rm -f /out/appsettings.Development.json /out/libmscordaccore.so /out/libmscordbi.so /out/libdbgshim.so /out/createdump \
           /out/libclrgc.so /out/libclrgcexp.so /out/libcoreclrtraceptprovider.so

# Chiseled = no shell, no package manager, non-root user (1654) by default. It includes OpenSSL and the CA bundle for TLS to AWS.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
COPY --from=build /out .
# Diagnostics off: no debugger/dump IPC socket, fewer threads, less memory. GC tuning (workstation, non-concurrent) is in the project file.
ENV DOTNET_EnableDiagnostics=0 \
    DOTNET_TieredPGO=0
EXPOSE 8080
ENTRYPOINT ["./EksDevopsOperator"]
