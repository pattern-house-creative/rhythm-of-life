-- Optional OpenIddict OAuth state; existing principals and tokens are unchanged.
-- Generated from OAuthDb with OpenIddict 7.9.0 and Npgsql EF Core 10.0.2.
CREATE TABLE oauth_applications (
    "Id" text NOT NULL,
    "ApplicationType" character varying(50),
    "ClientId" character varying(100),
    "ClientSecret" text,
    "ClientType" character varying(50),
    "ConcurrencyToken" character varying(50),
    "ConsentType" character varying(50),
    "DisplayName" text,
    "DisplayNames" text,
    "JsonWebKeySet" text,
    "Permissions" text,
    "PostLogoutRedirectUris" text,
    "Properties" text,
    "RedirectUris" text,
    "Requirements" text,
    "Settings" text,
    CONSTRAINT "PK_oauth_applications" PRIMARY KEY ("Id")
);


CREATE TABLE oauth_scopes (
    "Id" text NOT NULL,
    "ConcurrencyToken" character varying(50),
    "Description" text,
    "Descriptions" text,
    "DisplayName" text,
    "DisplayNames" text,
    "Name" character varying(200),
    "Properties" text,
    "Resources" text,
    CONSTRAINT "PK_oauth_scopes" PRIMARY KEY ("Id")
);


CREATE TABLE oauth_authorizations (
    "Id" text NOT NULL,
    "ApplicationId" text,
    "ConcurrencyToken" character varying(50),
    "CreationDate" timestamp with time zone,
    "Properties" text,
    "Scopes" text,
    "Status" character varying(50),
    "Subject" character varying(400),
    "Type" character varying(50),
    CONSTRAINT "PK_oauth_authorizations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_oauth_authorizations_oauth_applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES oauth_applications ("Id")
);


CREATE TABLE oauth_tokens (
    "Id" text NOT NULL,
    "ApplicationId" text,
    "AuthorizationId" text,
    "ConcurrencyToken" character varying(50),
    "CreationDate" timestamp with time zone,
    "ExpirationDate" timestamp with time zone,
    "Payload" text,
    "Properties" text,
    "RedemptionDate" timestamp with time zone,
    "ReferenceId" character varying(100),
    "Status" character varying(50),
    "Subject" character varying(400),
    "Type" character varying(150),
    CONSTRAINT "PK_oauth_tokens" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_oauth_tokens_oauth_applications_ApplicationId" FOREIGN KEY ("ApplicationId") REFERENCES oauth_applications ("Id"),
    CONSTRAINT "FK_oauth_tokens_oauth_authorizations_AuthorizationId" FOREIGN KEY ("AuthorizationId") REFERENCES oauth_authorizations ("Id")
);


CREATE UNIQUE INDEX "IX_oauth_applications_ClientId" ON oauth_applications ("ClientId");


CREATE INDEX "IX_oauth_authorizations_ApplicationId_Status_Subject_Type" ON oauth_authorizations ("ApplicationId", "Status", "Subject", "Type");


CREATE UNIQUE INDEX "IX_oauth_scopes_Name" ON oauth_scopes ("Name");


CREATE INDEX "IX_oauth_tokens_ApplicationId_Status_Subject_Type" ON oauth_tokens ("ApplicationId", "Status", "Subject", "Type");


CREATE INDEX "IX_oauth_tokens_AuthorizationId" ON oauth_tokens ("AuthorizationId");


CREATE UNIQUE INDEX "IX_oauth_tokens_ReferenceId" ON oauth_tokens ("ReferenceId");
