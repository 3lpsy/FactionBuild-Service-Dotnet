FROM mono:6

# when building, the default config will run dotnet restore
# if using a local/dev version of Faction.Common (as a volume), this will fail
# set this to 0 to disable restoring and publishing on build. should only be used locally
ARG PUBLISH_ENABLED=1

# the default run target just assumes the project has been built during build time
# valid run targets are: "watch" and "published"
# watch will watch for changes using dotnet watch and is best used for dev with volumes
ENV DOCKER_RUN_TARGET published

# if the image is built with PUBLISH_ENABLED=0, 
# then set DOCKER_PUBLISH_ON_RUN=1 to publish when run
# this allows for using compiled target but still a local version of Faction.Common
ENV DOCKER_PUBLISH_ON_RUN 0

# disable telemetry
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
# enable polling
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_USE_POLLING_FILE_WATCHER=true

# allows for mounting the dll from the host, used for development
RUN mkdir -p /Faction.Common/bin

# create expected directories to use
RUN mkdir -p /opt/faction/agents && \
    mkdir -p /opt/faction/moduels && \
    mkdir -p /opt/faction/agents/build

RUN apt-get update && \
    apt-get install wget gpg apt-transport-https apt-utils dirmngr -y && \
    wget -qO- https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor > microsoft.asc.gpg && \
    mv microsoft.asc.gpg /etc/apt/trusted.gpg.d/  && \
    wget -q https://packages.microsoft.com/config/debian/9/prod.list  && \
    mv prod.list /etc/apt/sources.list.d/microsoft-prod.list && \
    chown root:root /etc/apt/trusted.gpg.d/microsoft.asc.gpg && \
    chown root:root /etc/apt/sources.list.d/microsoft-prod.list && \
    apt-get update && \
    apt-get install dotnet-sdk-3.0 -y && \
    apt-get install python3 procps -y

# procps is used for file polling

WORKDIR /app

# copy csproj and restore as distinct layers
COPY ./Scripts/startup.sh /opt/startup.sh
# Possibly add dotnet tool install dotnet-ef --version 3.0 --tool-path /usr/local/bin/?
RUN chmod +x /opt/startup.sh

# copy csproj before the rest of the project to pull
# dependencies early to cache properly
COPY *.csproj ./
RUN /opt/startup.sh restore $PUBLISH_ENABLED

# copy and build everything else
COPY . ./
RUN rm -rf /app/bin && rm -rf /app/obj

# mark wait-for-it executable
RUN chmod 777 /app/Scripts/wait-for-it.sh

# publish if enabled
RUN /opt/startup.sh publish $PUBLISH_ENABLED

ENTRYPOINT [ "/bin/bash", "/opt/startup.sh" ]