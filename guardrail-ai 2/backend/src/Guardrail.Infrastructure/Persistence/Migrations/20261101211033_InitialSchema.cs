using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Guardrail.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    code = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_categories", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "merchants",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    logo_url = table.Column<string>(type: "text", nullable: true),
                    category_codes = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    provider_key = table.Column<string>(type: "text", nullable: false, defaultValue: "SEED"),
                    provider_merchant_ref = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_merchants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "citext", nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    access_failed_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.CheckConstraint("ck_users_display_name", "length(display_name) between 1 and 80");
                    table.CheckConstraint("ck_users_role", "role in ('USER','ADMIN')");
                });

            migrationBuilder.CreateTable(
                name: "products",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    brand = table.Column<string>(type: "text", nullable: true),
                    category_code = table.Column<string>(type: "text", nullable: false),
                    tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    unit_label = table.Column<string>(type: "text", nullable: true),
                    image_url = table.Column<string>(type: "text", nullable: true),
                    is_available = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    stock_quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('english', coalesce(name,'') || ' ' || coalesce(brand,'') || ' ' || coalesce(description,''))", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_products", x => x.id);
                    table.CheckConstraint("ck_products_stock_quantity", "stock_quantity >= 0");
                    table.CheckConstraint("ck_products_unit_price", "unit_price > 0");
                    table.ForeignKey(
                        name: "fk_products_categories_category_code",
                        column: x => x.category_code,
                        principalTable: "categories",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_products_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_conversations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_conversations", x => x.id);
                    table.CheckConstraint("ck_agent_conversations_status", "status in ('ACTIVE','ARCHIVED')");
                    table.ForeignKey(
                        name: "fk_agent_conversations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: true),
                    before_hash = table.Column<string>(type: "char(64)", nullable: true),
                    after_hash = table.Column<string>(type: "char(64)", nullable: true),
                    data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<IPAddress>(type: "inet", nullable: true),
                    user_agent = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => x.id);
                    table.CheckConstraint("ck_audit_logs_actor_type", "actor_type in ('USER','AGENT','SYSTEM','PAYPAL')");
                    table.ForeignKey(
                        name: "fk_audit_logs_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_keys",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    request_path = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "char(64)", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: true),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_keys", x => x.id);
                    table.CheckConstraint("ck_idempotency_keys_key", "length(key) between 8 and 100");
                    table.ForeignKey(
                        name: "fk_idempotency_keys_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "char(64)", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    replaced_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_refresh_tokens_replaced_by_id",
                        column: x => x.replaced_by_id,
                        principalTable: "refresh_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    tool_call_id = table.Column<string>(type: "text", nullable: true),
                    cards = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    token_usage = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_messages", x => x.id);
                    table.CheckConstraint("ck_agent_messages_role", "role in ('USER','ASSISTANT','TOOL')");
                    table.ForeignKey(
                        name: "fk_agent_messages_agent_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "agent_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_messages_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shopping_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shopping_sessions", x => x.id);
                    table.CheckConstraint("ck_shopping_sessions_status", "status in ('ACTIVE','CLOSED')");
                    table.ForeignKey(
                        name: "fk_shopping_sessions_agent_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "agent_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shopping_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "agent_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "text", nullable: false),
                    tool_call_id = table.Column<string>(type: "text", nullable: false),
                    arguments = table.Column<string>(type: "jsonb", nullable: false),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_executions", x => x.id);
                    table.CheckConstraint("ck_agent_executions_status", "status in ('SUCCEEDED','REJECTED','FAILED')");
                    table.ForeignKey(
                        name: "fk_agent_executions_agent_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "agent_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_agent_executions_agent_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "agent_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_agent_executions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shopping_carts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shopping_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    subtotal = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    created_by_actor = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shopping_carts", x => x.id);
                    table.CheckConstraint("ck_shopping_carts_created_by_actor", "created_by_actor in ('USER','AGENT','SYSTEM','PAYPAL')");
                    table.CheckConstraint("ck_shopping_carts_status", "status in ('OPEN','LOCKED','CHECKED_OUT','ABANDONED')");
                    table.ForeignKey(
                        name: "fk_shopping_carts_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shopping_carts_shopping_sessions_shopping_session_id",
                        column: x => x.shopping_session_id,
                        principalTable: "shopping_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_shopping_carts_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cart_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    product_name = table.Column<string>(type: "text", nullable: false),
                    category_code = table.Column<string>(type: "text", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cart_items", x => x.id);
                    table.CheckConstraint("ck_cart_items_quantity", "quantity between 1 and 99");
                    table.ForeignKey(
                        name: "fk_cart_items_categories_category_code",
                        column: x => x.category_code,
                        principalTable: "categories",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cart_items_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_cart_items_shopping_carts_cart_id",
                        column: x => x.cart_id,
                        principalTable: "shopping_carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "approval_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_version = table.Column<int>(type: "integer", nullable: false),
                    cart_snapshot_hash = table.Column<string>(type: "char(64)", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    rejection_reason = table.Column<string>(type: "text", nullable: true),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    invalidated_reason = table.Column<string>(type: "text", nullable: true),
                    requested_by_actor = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_requests", x => x.id);
                    table.CheckConstraint("ck_approval_requests_requested_by_actor", "requested_by_actor in ('USER','AGENT','SYSTEM','PAYPAL')");
                    table.CheckConstraint("ck_approval_requests_status", "status in ('PENDING','APPROVED','REJECTED','EXPIRED','INVALIDATED','CONSUMED')");
                    table.ForeignKey(
                        name: "fk_approval_requests_carts_cart_id",
                        column: x => x.cart_id,
                        principalTable: "shopping_carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approval_requests_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_approval_requests_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "text", nullable: false, defaultValue: "PAYPAL"),
                    intent = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    authorized_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    captured_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    refunded_amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false, defaultValue: 0m),
                    paypal_order_id = table.Column<string>(type: "text", nullable: true),
                    paypal_authorization_id = table.Column<string>(type: "text", nullable: true),
                    authorization_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    paypal_capture_id = table.Column<string>(type: "text", nullable: true),
                    paypal_create_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    paypal_authorize_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paypal_capture_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    paypal_void_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    final_check_result = table.Column<string>(type: "jsonb", nullable: true),
                    approve_url = table.Column<string>(type: "text", nullable: true),
                    paypal_payer_id = table.Column<string>(type: "text", nullable: true),
                    last_paypal_debug_id = table.Column<string>(type: "text", nullable: true),
                    last_paypal_response = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_payments", x => x.id);
                    table.CheckConstraint("ck_payments_intent", "intent in ('CAPTURE','AUTHORIZE')");
                    table.CheckConstraint("ck_payments_status", "status in ('INITIATED','CREATED','PAYER_ACTION_REQUIRED','APPROVED','AUTHORIZED','CAPTURE_PENDING','COMPLETED','DECLINED','FAILED','VOIDED','REFUNDED','PARTIALLY_REFUNDED','REVERSED')");
                    table.ForeignKey(
                        name: "fk_payments_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "paypal_webhook_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    paypal_event_id = table.Column<string>(type: "text", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    resource_type = table.Column<string>(type: "text", nullable: true),
                    resource_id = table.Column<string>(type: "text", nullable: true),
                    transmission_id = table.Column<string>(type: "text", nullable: true),
                    verification_status = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    raw_body = table.Column<string>(type: "text", nullable: false),
                    headers = table.Column<string>(type: "jsonb", nullable: false),
                    related_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_paypal_webhook_events", x => x.id);
                    table.CheckConstraint("ck_paypal_webhook_events_status", "status in ('RECEIVED','PROCESSED','IGNORED','FAILED','REJECTED')");
                    table.CheckConstraint("ck_paypal_webhook_events_verification_status", "verification_status in ('SUCCESS','FAILURE','ERROR')");
                    table.ForeignKey(
                        name: "fk_paypal_webhook_events_payments_related_payment_id",
                        column: x => x.related_payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_evaluations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_version = table.Column<int>(type: "integer", nullable: false),
                    cart_snapshot_hash = table.Column<string>(type: "char(64)", nullable: false),
                    cart_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    reasons = table.Column<string>(type: "jsonb", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    budget_impacts = table.Column<string>(type: "jsonb", nullable: false),
                    requested_by_actor = table.Column<string>(type: "text", nullable: false),
                    agent_execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    evaluated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_evaluations", x => x.id);
                    table.CheckConstraint("ck_policy_evaluations_decision", "decision in ('ALLOWED','REQUIRES_APPROVAL','BLOCKED')");
                    table.CheckConstraint("ck_policy_evaluations_requested_by_actor", "requested_by_actor in ('USER','AGENT','SYSTEM','PAYPAL')");
                    table.ForeignKey(
                        name: "fk_policy_evaluations_carts_cart_id",
                        column: x => x.cart_id,
                        principalTable: "shopping_carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_policy_evaluations_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    spending_profile_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    snapshot_hash = table.Column<string>(type: "char(64)", nullable: false),
                    change_summary = table.Column<string>(type: "text", nullable: false),
                    created_by_actor = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_policy_versions", x => x.id);
                    table.CheckConstraint("ck_policy_versions_created_by_actor", "created_by_actor in ('USER','AGENT','SYSTEM','PAYPAL')");
                    table.CheckConstraint("ck_policy_versions_version_number", "version_number >= 1");
                    table.ForeignKey(
                        name: "fk_policy_versions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cart_version = table.Column<int>(type: "integer", nullable: false),
                    cart_snapshot_hash = table.Column<string>(type: "char(64)", nullable: false),
                    policy_evaluation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    budget_week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    budget_month_start = table.Column<DateOnly>(type: "date", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    checkout_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_purchases", x => x.id);
                    table.CheckConstraint("ck_purchases_amount", "amount > 0");
                    table.CheckConstraint("ck_purchases_status", "status in ('AWAITING_APPROVAL','READY_FOR_CHECKOUT','CHECKOUT_PENDING','PAYER_APPROVED','PAYMENT_AUTHORIZED','CAPTURE_PENDING','COMPLETED','REJECTED','CANCELLED','EXPIRED','FAILED','REFUNDED','PARTIALLY_REFUNDED')");
                    table.ForeignKey(
                        name: "fk_purchases_carts_cart_id",
                        column: x => x.cart_id,
                        principalTable: "shopping_carts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchases_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchases_policy_evaluations_policy_evaluation_id",
                        column: x => x.policy_evaluation_id,
                        principalTable: "policy_evaluations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchases_policy_versions_policy_version_id",
                        column: x => x.policy_version_id,
                        principalTable: "policy_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_purchases_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "spending_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_type = table.Column<string>(type: "text", nullable: false),
                    category_code = table.Column<string>(type: "text", nullable: true),
                    merchant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parameters = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spending_policies", x => x.id);
                    table.CheckConstraint("ck_spending_policies_rule_type", "rule_type in ('CATEGORY_BUDGET','SPENDING_LIMIT','MAX_TRANSACTION_AMOUNT','APPROVAL_THRESHOLD','MERCHANT_ALLOW','MERCHANT_BLOCK','NEW_MERCHANT_POLICY','PRODUCT_RESTRICTION','DUPLICATE_PURCHASE','TIME_RESTRICTION','UNBUDGETED_CATEGORY_POLICY')");
                    table.ForeignKey(
                        name: "fk_spending_policies_categories_category_code",
                        column: x => x.category_code,
                        principalTable: "categories",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_spending_policies_merchants_merchant_id",
                        column: x => x.merchant_id,
                        principalTable: "merchants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_spending_policies_policy_versions_policy_version_id",
                        column: x => x.policy_version_id,
                        principalTable: "policy_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_spending_policies_products_product_id",
                        column: x => x.product_id,
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_spending_policies_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "spending_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false, defaultValue: "USD"),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    current_policy_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spending_profiles", x => x.id);
                    table.ForeignKey(
                        name: "fk_profile_current_version",
                        column: x => x.current_policy_version_id,
                        principalTable: "policy_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_spending_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "spending_ledger_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_code = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false),
                    transition = table.Column<string>(type: "text", nullable: false),
                    from_bucket = table.Column<string>(type: "text", nullable: false),
                    to_bucket = table.Column<string>(type: "text", nullable: false),
                    budget_week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    budget_month_start = table.Column<DateOnly>(type: "date", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    external_ref = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_spending_ledger_entries", x => x.id);
                    table.CheckConstraint("ck_spending_ledger_entries_amount", "amount > 0");
                    table.CheckConstraint("ck_spending_ledger_entries_from_bucket", "from_bucket in ('NONE','PROPOSED','RESERVED','AUTHORIZED','CAPTURED','RELEASED','FAILED','REFUNDED')");
                    table.CheckConstraint("ck_spending_ledger_entries_source", "source in ('SYSTEM','USER','PAYPAL_API','PAYPAL_WEBHOOK','RECONCILER','SWEEPER')");
                    table.CheckConstraint("ck_spending_ledger_entries_to_bucket", "to_bucket in ('NONE','PROPOSED','RESERVED','AUTHORIZED','CAPTURED','RELEASED','FAILED','REFUNDED')");
                    table.CheckConstraint("ck_spending_ledger_entries_transition", "transition in ('PROPOSE','RESERVE','CONFIRM_RESERVATION','RELEASE','AUTHORIZE','VOID','CAPTURE','FAIL','REFUND')");
                    table.ForeignKey(
                        name: "fk_spending_ledger_entries_categories_category_code",
                        column: x => x.category_code,
                        principalTable: "categories",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_spending_ledger_entries_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_spending_ledger_entries_purchases_purchase_id",
                        column: x => x.purchase_id,
                        principalTable: "purchases",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_spending_ledger_entries_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_conversations_user_id_last_message_at",
                table: "agent_conversations",
                columns: new[] { "user_id", "last_message_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_exec_conv",
                table: "agent_executions",
                columns: new[] { "conversation_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_agent_executions_message_id",
                table: "agent_executions",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_executions_user_id",
                table: "agent_executions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_messages_conversation_id_sequence",
                table: "agent_messages",
                columns: new[] { "conversation_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_messages_user_id",
                table: "agent_messages",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_cart_id",
                table: "approval_requests",
                column: "cart_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_merchant_id",
                table: "approval_requests",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_policy_evaluation_id",
                table: "approval_requests",
                column: "policy_evaluation_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_policy_version_id",
                table: "approval_requests",
                column: "policy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_purchase_id",
                table: "approval_requests",
                column: "purchase_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_approval_requests_user_id_status_expires_at",
                table: "approval_requests",
                columns: new[] { "user_id", "status", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_expiry",
                table: "approval_requests",
                column: "expires_at",
                filter: "status in ('PENDING','APPROVED')");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entity",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_user_action_time",
                table: "audit_logs",
                columns: new[] { "user_id", "action", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_user_time",
                table: "audit_logs",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_cart_id_product_id",
                table: "cart_items",
                columns: new[] { "cart_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_category_code",
                table: "cart_items",
                column: "category_code");

            migrationBuilder.CreateIndex(
                name: "ix_cart_items_product_id",
                table: "cart_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_keys_user_id_key_request_path",
                table: "idempotency_keys",
                columns: new[] { "user_id", "key", "request_path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_merchants_slug",
                table: "merchants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_paypal_authorization_id",
                table: "payments",
                column: "paypal_authorization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_paypal_authorize_request_id",
                table: "payments",
                column: "paypal_authorize_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_paypal_capture_id",
                table: "payments",
                column: "paypal_capture_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_paypal_capture_request_id",
                table: "payments",
                column: "paypal_capture_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_paypal_create_request_id",
                table: "payments",
                column: "paypal_create_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_paypal_order_id",
                table: "payments",
                column: "paypal_order_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_paypal_void_request_id",
                table: "payments",
                column: "paypal_void_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_payments_user_id",
                table: "payments",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_payments_one_active_per_purchase",
                table: "payments",
                column: "purchase_id",
                unique: true,
                filter: "status not in ('DECLINED','FAILED','VOIDED')");

            migrationBuilder.CreateIndex(
                name: "ix_paypal_webhook_events_paypal_event_id",
                table: "paypal_webhook_events",
                column: "paypal_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_paypal_webhook_events_related_payment_id",
                table: "paypal_webhook_events",
                column: "related_payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_webhooks_pending",
                table: "paypal_webhook_events",
                columns: new[] { "status", "received_at" },
                filter: "status = 'RECEIVED'");

            migrationBuilder.CreateIndex(
                name: "ix_policy_evaluations_cart_id_cart_version",
                table: "policy_evaluations",
                columns: new[] { "cart_id", "cart_version" });

            migrationBuilder.CreateIndex(
                name: "ix_policy_evaluations_policy_version_id",
                table: "policy_evaluations",
                column: "policy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_policy_evaluations_user_id_evaluated_at",
                table: "policy_evaluations",
                columns: new[] { "user_id", "evaluated_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_policy_versions_spending_profile_id_version_number",
                table: "policy_versions",
                columns: new[] { "spending_profile_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_policy_versions_user_id_version_number",
                table: "policy_versions",
                columns: new[] { "user_id", "version_number" });

            migrationBuilder.CreateIndex(
                name: "ix_products_category_code_unit_price",
                table: "products",
                columns: new[] { "category_code", "unit_price" });

            migrationBuilder.CreateIndex(
                name: "ix_products_merchant_id",
                table: "products",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_products_merchant_id_sku",
                table: "products",
                columns: new[] { "merchant_id", "sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_products_search_vector",
                table: "products",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "ix_purchases_cart_id_cart_version",
                table: "purchases",
                columns: new[] { "cart_id", "cart_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_purchases_checkout_expires_at",
                table: "purchases",
                column: "checkout_expires_at",
                filter: "status in ('READY_FOR_CHECKOUT','CHECKOUT_PENDING')");

            migrationBuilder.CreateIndex(
                name: "ix_purchases_merchant_id",
                table: "purchases",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchases_policy_evaluation_id",
                table: "purchases",
                column: "policy_evaluation_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchases_policy_version_id",
                table: "purchases",
                column: "policy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_purchases_user_id_status_created_at",
                table: "purchases",
                columns: new[] { "user_id", "status", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_family_id",
                table: "refresh_tokens",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_replaced_by_id",
                table: "refresh_tokens",
                column: "replaced_by_id");

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id",
                table: "refresh_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_shopping_carts_merchant_id",
                table: "shopping_carts",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_shopping_carts_shopping_session_id",
                table: "shopping_carts",
                column: "shopping_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_shopping_carts_user_id_status",
                table: "shopping_carts",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_shopping_sessions_conversation_id",
                table: "shopping_sessions",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_shopping_sessions_user_id_conversation_id",
                table: "shopping_sessions",
                columns: new[] { "user_id", "conversation_id" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_user_month",
                table: "spending_ledger_entries",
                columns: new[] { "user_id", "budget_month_start", "category_code" });

            migrationBuilder.CreateIndex(
                name: "ix_ledger_user_week",
                table: "spending_ledger_entries",
                columns: new[] { "user_id", "budget_week_start", "category_code" });

            migrationBuilder.CreateIndex(
                name: "ix_spending_ledger_entries_category_code",
                table: "spending_ledger_entries",
                column: "category_code");

            migrationBuilder.CreateIndex(
                name: "ix_spending_ledger_entries_payment_id",
                table: "spending_ledger_entries",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_spending_ledger_entries_purchase_id_category_code_transitio",
                table: "spending_ledger_entries",
                columns: new[] { "purchase_id", "category_code", "transition", "external_ref" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_spending_policies_category_code",
                table: "spending_policies",
                column: "category_code");

            migrationBuilder.CreateIndex(
                name: "ix_spending_policies_merchant_id",
                table: "spending_policies",
                column: "merchant_id");

            migrationBuilder.CreateIndex(
                name: "ix_spending_policies_policy_version_id",
                table: "spending_policies",
                column: "policy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_spending_policies_product_id",
                table: "spending_policies",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_spending_policies_user_id",
                table: "spending_policies",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_spending_profiles_current_policy_version_id",
                table: "spending_profiles",
                column: "current_policy_version_id");

            migrationBuilder.CreateIndex(
                name: "ix_spending_profiles_user_id",
                table: "spending_profiles",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_approval_requests_policy_evaluations_policy_evaluation_id",
                table: "approval_requests",
                column: "policy_evaluation_id",
                principalTable: "policy_evaluations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_approval_requests_policy_versions_policy_version_id",
                table: "approval_requests",
                column: "policy_version_id",
                principalTable: "policy_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_approval_requests_purchases_purchase_id",
                table: "approval_requests",
                column: "purchase_id",
                principalTable: "purchases",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_payments_purchases_purchase_id",
                table: "payments",
                column: "purchase_id",
                principalTable: "purchases",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_policy_evaluations_policy_versions_policy_version_id",
                table: "policy_evaluations",
                column: "policy_version_id",
                principalTable: "policy_versions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_policy_versions_spending_profiles_spending_profile_id",
                table: "policy_versions",
                column: "spending_profile_id",
                principalTable: "spending_profiles",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_policy_versions_users_user_id",
                table: "policy_versions");

            migrationBuilder.DropForeignKey(
                name: "fk_spending_profiles_users_user_id",
                table: "spending_profiles");

            migrationBuilder.DropForeignKey(
                name: "fk_profile_current_version",
                table: "spending_profiles");

            migrationBuilder.DropTable(
                name: "agent_executions");

            migrationBuilder.DropTable(
                name: "approval_requests");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "cart_items");

            migrationBuilder.DropTable(
                name: "idempotency_keys");

            migrationBuilder.DropTable(
                name: "paypal_webhook_events");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "spending_ledger_entries");

            migrationBuilder.DropTable(
                name: "spending_policies");

            migrationBuilder.DropTable(
                name: "agent_messages");

            migrationBuilder.DropTable(
                name: "payments");

            migrationBuilder.DropTable(
                name: "products");

            migrationBuilder.DropTable(
                name: "purchases");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "policy_evaluations");

            migrationBuilder.DropTable(
                name: "shopping_carts");

            migrationBuilder.DropTable(
                name: "merchants");

            migrationBuilder.DropTable(
                name: "shopping_sessions");

            migrationBuilder.DropTable(
                name: "agent_conversations");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "policy_versions");

            migrationBuilder.DropTable(
                name: "spending_profiles");
        }
    }
}
