# Sabotage record — contract-005 · T-12 (G-11)

Every end-to-end test has a named sabotage: one thing the harness weakens for that test alone — in its inputs, a
container's environment or the network — when `MCP_E2E_SABOTAGE` names it, with no code edited. Each is declared on
its test (`[Sabotage]`, `McpServerTemplate.E2E/Harness/Sabotage.cs`), and each was run on its own, with the test it
targets and nothing else. A red below counts only when the sabotage acted and the test failed on the assertion that
carries its claim: a `ClaimException`, which only `Claim` throws. A red at a self-check, a positive control or
startup fails with another exception, and is not one. Each message is the runner's, with any token in it replaced by
`[token]`.

A message is the test's own, and says what the test was checking, not what the sabotage did. Where a sabotage leaves
out the misconfiguration a startup test must refuse (`…-left-out`), the message still names that setting — "BindAddress
is 255.255.255.255: the server started" — but the server that started ran without it, on the environment's own settings:
the product refused none of them, and was given none of them. Each entry's "Acts on" line says what its sabotage did.

Each entry says when, at which commit and on which version of its test's file (the first 12 hex digits of the SHA-256
of `McpServerTemplate.E2E/{Class}.cs`, line endings aside) its red was taken. An entry is retaken when it is missing,
when its test's file has changed since, or when it was not red; the others are kept as they stand. A change to the
harness or the product marks no entry stale: after one, `--all` retakes every red. Written by
`scripts/e2e-sabotage.sh`, and held to by `HarnessSelfTests`: the suite fails while an entry is missing, stale or
not red. CI refuses `MCP_E2E_SABOTAGE`.

- 100 sabotages over the 99 end-to-end tests the runner lists, theory rows included: 99 red on their test's claim, 0 not, 1 held
- Reds taken from 2026-09-29T04:06:23Z to 2026-09-29T15:09:56Z
- Not end-to-end, so no sabotage: `HarnessSelfTests` (11 tests). In-process checks of the harness's own code — the settings delta, the revision label, the sabotage registry and its record: they start no container and send nothing to the image, so there is no fixture input, container environment or network for a sabotage to act on, and no claim about the image for it to break.

| Sabotage | Test | Acts on | Test file | Result |
|---|---|---|---|---|
| `t11-4-idp-b-issuer-pinned-as-its-authority` | `AuthorizationServersTests.T11_4_the_metadata_lists_each_identity_providers_issuer` | container environment | `d5506b15ad6c` | red on its claim |
| `t11-4-http-issuer-left-out` | `AuthorizationServersTests.T11_4_an_http_issuer_refuses_to_start` | container environment | `d5506b15ad6c` | red on its claim |
| `t11-4-issuer-query-left-out` | `AuthorizationServersTests.T11_4_an_issuer_with_a_query_refuses_to_start` | container environment | `d5506b15ad6c` | red on its claim |
| `t11-3-keycloak-client-claim-left-as-azp` | `ClientClaimTests.T11_3_a_token_without_the_claim_that_ClientIdClaim_names_is_refused` | container environment | `4d0782cafbcc` | red on its claim |
| `t11-3-client-claim-left-out.sub` | `ClientClaimTests.T11_3_a_client_claim_that_is_always_present_or_means_something_else_refuses_to_start` | container environment | `4d0782cafbcc` | red on its claim |
| `t11-3-client-claim-left-out.scope` | `ClientClaimTests.T11_3_a_client_claim_that_is_always_present_or_means_something_else_refuses_to_start` | container environment | `4d0782cafbcc` | red on its claim |
| `t11-3-client-claim-left-out.typ` | `ClientClaimTests.T11_3_a_client_claim_that_is_always_present_or_means_something_else_refuses_to_start` | container environment | `4d0782cafbcc` | red on its claim |
| `t17-key-written-in-the-profile` | `DataProtectionTests.T17_the_image_writes_no_key_and_loads_no_key_ring` | container environment | `821edadb8e83` | red on its claim |
| `t9-documented-resource-at-the-root` | `DocsProfileTests.T9_the_documented_deployment_starts_accepts_a_real_token_and_lists_tools` | inputs | `a91a6538a94e` | red on its claim |
| `t11-1-resource-at-the-root-left-out` | `EndpointAndResourceTests.T11_1_a_resource_whose_path_is_not_mcp_refuses_to_start` | container environment | `aba186f1fecb` | red on its claim |
| `t11-1-dot-segment-left-out` | `EndpointAndResourceTests.T11_1_a_resource_whose_path_is_mcp_only_once_parsed_refuses_to_start` | container environment | `aba186f1fecb` | red on its claim |
| `t11-1-client-dials-the-root` | `EndpointAndResourceTests.T11_1_the_challenge_at_the_resource_url_and_the_rfc9728_location_answer_with_the_same_document` | inputs | `aba186f1fecb` | red on its claim |
| `t11-2-open-variant-left-out.no-allowed-hosts` | `HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-open-variant-left-out.allowed-ipv4-any` | `HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-open-variant-left-out.allowed-star` | `HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-open-variant-left-out.allowed-ipv6-any-bracketed` | `HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-open-variant-left-out.allowed-ipv6-any` | `HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-not-one-name-left-out.subdomain-wildcard-tld` | `HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-not-one-name-left-out.wildcard-root-dot` | `HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-not-one-name-left-out.subdomain-wildcard` | `HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-not-one-name-left-out.fullwidth-star` | `HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-not-one-name-left-out.trailing-dot` | `HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-not-one-name-left-out.unset-variable` | `HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unservable-setting-left-out.bind-broadcast` | `HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unservable-setting-left-out.bind-multicast` | `HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unservable-setting-left-out.proxy-not-an-address` | `HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unservable-setting-left-out.network-prefix-too-long` | `HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unbindable-address-left-out.bind-link-local` | `HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unbindable-address-left-out.bind-link-local-unknown-zone` | `HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unbindable-address-left-out.bind-not-held` | `HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-unbindable-address-left-out.bind-octal` | `HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-bind-path-left-out` | `HostFilteringTests.T11_2_a_bind_address_with_a_path_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-empty-bind-left-out` | `HostFilteringTests.T11_2_an_empty_bind_address_refuses_to_start_naming_the_key` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-ipv4-mapped-bind-left-out.ipv4-mapped-bracketed` | `HostFilteringTests.T11_2_an_ipv4_mapped_bind_address_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-ipv4-mapped-bind-left-out.ipv4-mapped` | `HostFilteringTests.T11_2_an_ipv4_mapped_bind_address_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-kestrel-endpoint-left-out` | `HostFilteringTests.T11_2_a_kestrel_setting_refuses_to_start` | container environment | `cf1f86416d62` | red on its claim |
| `t11-2-attacker-host-allowed` | `HostFilteringTests.T11_2_once_a_real_name_is_set_a_foreign_host_is_refused` | container environment | `cf1f86416d62` | red on its claim |
| `t7-server-b-counts-in-the-environments-redis` | `LimitsTests.T7_one_callers_count_carries_across_both_servers_while_other_callers_are_served` | container environment | `f5765f545a19` | red on its claim |
| `t7-other-caller-minted-for-the-same-subject` | `LimitsTests.T7_one_callers_count_carries_across_both_servers_while_other_callers_are_served` | inputs | `f5765f545a19` | red on its claim |
| `t7-second-address-forwarded-as-the-first` | `LimitsTests.T7_one_forwarded_address_gets_429_on_its_61st_request_while_another_gets_200` | inputs | `f5765f545a19` | red on its claim |
| `t7-redis-left-running` | `LimitsTests.T7_with_redis_stopped_requests_are_refused_limits_unavailable` | container environment | `f5765f545a19` | red on its claim |
| `t11-5-sink-path-writable` | `LogSinkTests.T11_5_a_file_sink_at_another_index_that_cannot_write_refuses_to_start_naming_the_resolved_path` | container environment | `0314aa233869` | red on its claim |
| `t11-5-variable-stays-inside-logs` | `LogSinkTests.T11_5_a_file_sink_whose_path_climbs_out_through_an_environment_variable_refuses_to_start` | container environment | `0314aa233869` | red on its claim |
| `settings-read-once-idp-b-keys-from-idp-a` | `SettingsReadOnceTests.A_settings_file_changed_while_the_server_runs_changes_nothing_until_it_restarts` | container environment | `9bdfdd724cf1` | red on its claim |
| `t2-hidden-tool-and-unscoped-prompt-callers-given-weather-read` | `ShippedImageRefusalTests.T2_contract_003s_refusals_each_carry_their_rule_return_nothing_and_reach_no_upstream` | inputs | `17a81a500256` | red on its claim |
| `t4-weather-left-bound-to-keycloak` | `StandardClientTests.T4_the_sdk_client_given_only_the_resource_url_discovers_authorizes_and_calls_a_tool` | container environment | `acd2efee5b48` | red on its claim |
| `t10-credential-left-out.resource` | `StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential` | container environment | `545dca84e634` | red on its claim |
| `t10-credential-left-out.authority` | `StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential` | container environment | `545dca84e634` | red on its claim |
| `t10-credential-left-out.issuer` | `StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential` | container environment | `545dca84e634` | red on its claim |
| `t10-credential-left-out.base-url` | `StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential` | container environment | `545dca84e634` | red on its claim |
| `t10-credential-left-out.authority-query` | `StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential` | container environment | `545dca84e634` | red on its claim |
| `t10-credential-left-out.base-url-query` | `StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential` | container environment | `545dca84e634` | red on its claim |
| `t10-forwarded-headers-switch-left-out` | `StartupAndShutdownTests.T10_the_frameworks_forwarded_headers_switch_exits_78_naming_where_it_came_from` | container environment | `545dca84e634` | red on its claim |
| `t10-staging-with-no-proxy-left-out` | `StartupAndShutdownTests.T10_outside_development_a_server_with_no_proxy_it_trusts_exits_78` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.unknown-setting` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.production-without-redis` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.http-authority` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.missing-allowed-hosts` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.shutdown-timeout-set` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.known-network-of-every-address` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.known-network-of-every-ipv6-address` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.known-network-past-its-prefix` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.known-network-broader-than-a-slash-8` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-misconfiguration-left-out.two-identity-providers-one-issuer` | `StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause` | container environment | `545dca84e634` | red on its claim |
| `t10-stop-signal-swallowed` | `StartupAndShutdownTests.T10_docker_stop_ends_the_server_cleanly_within_the_grace_with_a_shutdown_line` | inputs | `545dca84e634` | red on its claim |
| `t10-busy-stop-signal-swallowed` | `StartupAndShutdownTests.T10_docker_stop_with_a_request_in_flight_ends_the_server_cleanly_within_the_grace` | inputs | `545dca84e634` | red on its claim |
| `t10-request-kind-sent-as-ping.resources-subscribe` | `StartupAndShutdownTests.T10_a_request_kind_nobody_governs_is_refused_request_kind` | inputs | `545dca84e634` | red on its claim |
| `t10-request-kind-sent-as-ping.logging-setlevel` | `StartupAndShutdownTests.T10_a_request_kind_nobody_governs_is_refused_request_kind` | inputs | `545dca84e634` | red on its claim |
| `t10-request-kind-sent-as-ping.e2e-no-such-method` | `StartupAndShutdownTests.T10_a_request_kind_nobody_governs_is_refused_request_kind` | inputs | `545dca84e634` | red on its claim |
| `t8-expired-attempt-sent-before-it-expires` | `TestHostConfirmationTests.T8_five_tampering_attempts_are_each_refused_for_their_own_reason_and_the_tool_ran_once` | inputs | `601b1c7d72b1` | red on its claim |
| `t8-unscoped-caller-given-demo-read` | `TestHostTests.T8_an_unscoped_completion_is_refused_not_permitted_with_no_result_and_its_refusal_is_on_stderr` | inputs | `1f45580db716` | red on its claim |
| `t8-authority-under-test.production` | `TestHostTests.T8_the_test_host_exits_78_given_an_identity_provider_outside_test` | container environment | `1f45580db716` | red on its claim |
| `t8-authority-under-test.staging` | `TestHostTests.T8_the_test_host_exits_78_given_an_identity_provider_outside_test` | container environment | `1f45580db716` | red on its claim |
| `t8-test-host-also-serves-jsonplaceholder` | `TestHostTests.T8_the_test_hosts_frame_line_is_the_shipped_images_but_for_the_test_modules` | container environment | `1f45580db716` | red on its claim |
| `t3-no-token-sent-straight-to-the-server` | `TokenRefusalTests.T3_no_token_is_challenged_with_the_metadata_on_the_front` | network | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.wrong-audience` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.expired` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.alg-none` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.hs256` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.cross-signed` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.missing-claim-sub` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.missing-claim-jti` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.missing-claim-client-id` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-bad-token-minted-valid.missing-claim-iat` | `TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-unattributable-token-sent-valid.unreadable` | `TokenRefusalTests.T3_a_token_the_server_cannot_attribute_is_refused_with_its_reason_in_its_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-unattributable-token-sent-valid.oversized` | `TokenRefusalTests.T3_a_token_the_server_cannot_attribute_is_refused_with_its_reason_in_its_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-unattributable-token-sent-valid.no-issuer` | `TokenRefusalTests.T3_a_token_the_server_cannot_attribute_is_refused_with_its_reason_in_its_log` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-key-confusion-token-minted-valid` | `TokenRefusalTests.T3_a_key_confusion_token_is_refused_and_its_refusal_logged` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-stale-token-issued-a-minute-ago` | `TokenRefusalTests.T3_a_write_tool_called_with_a_token_issued_six_minutes_ago_is_refused_for_its_age` | inputs | `33fbb5bb5932` | red on its claim |
| `t3-stranger-registered-on-the-server` | `TokenRefusalTests.T3_an_unregistered_issuers_token_costs_no_key_lookup_at_any_issuer` | container environment | `33fbb5bb5932` | red on its claim |
| `t5-other-caller-minted-at-idp-a` | `TrustDomainTests.T5_a_caller_from_the_other_issuer_sees_none_of_the_bound_providers_items_in_the_lists` | inputs | `67939ef2a0b6` | red on its claim |
| `t5-caller-minted-at-idp-a.resource` | `TrustDomainTests.T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist` | inputs | `67939ef2a0b6` | red on its claim |
| `t5-caller-minted-at-idp-a.prompt` | `TrustDomainTests.T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist` | inputs | `67939ef2a0b6` | red on its claim |
| `t5-caller-minted-at-idp-a.tool` | `TrustDomainTests.T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist` | inputs | - | held |
| `t15-observations-host-left-to-the-fake` | `UpstreamExtensionPointTests.T15_a_stand_in_registered_for_a_provider_host_receives_the_servers_call` | network | `50ec9f563473` | red on its claim |
| `t1-keycloak-issuer-pinned-to-another-realm` | `WalkingSkeletonTests.T1_a_keycloak_token_lists_tools_through_the_front` | container environment | `8499492da996` | red on its claim |
| `t1-idp-a-reached-by-another-name` | `WalkingSkeletonTests.T1_the_test_issuer_is_one_issuer_by_one_name_from_the_server_and_from_the_test` | container environment | `8499492da996` | red on its claim |
| `t1-authorization-server-outside-the-environment` | `WalkingSkeletonTests.T1_the_sdk_clients_traffic_and_its_oauth_discovery_go_through_the_name_map` | container environment | `8499492da996` | red on its claim |
| `t1-second-address-forwarded-as-the-first` | `WalkingSkeletonTests.T1_the_fronts_forwarded_address_is_honoured` | inputs | `8499492da996` | red on its claim |
| `t1-gateway-trusted-as-a-proxy` | `WalkingSkeletonTests.T1_forwarded_headers_sent_straight_to_the_server_are_ignored` | container environment | `8499492da996` | red on its claim |

## Each sabotage, and its red

### `t11-4-idp-b-issuer-pinned-as-its-authority`

- Test: `McpServerTemplate.E2E.AuthorizationServersTests.T11_4_the_metadata_lists_each_identity_providers_issuer`
- Acts on container environment: The class's server pins idp-b's issuer as its authority, https://idp-b.e2e.test without the trailing slash, which is then what it lists: not the issuer idp-b's tokens and its RFC 8414 metadata carry.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.1 s
- Taken 2026-09-29T04:06:23Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `d5506b15ad6c`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : authorization_servers is [https://idp-a.e2e.test, https://idp-b.e2e.test, https://keycloak.e2e.test:8443/realms/mcp], not the identity providers' issuers [https://idp-a.e2e.test, https://idp-b.e2e.test/, https://keycloak.e2e.test:8443/realms/mcp]: idp-b's issuer is https://idp-b.e2e.test/ and its authority https://idp-b.e2e.test.
~~~

### `t11-4-http-issuer-left-out`

- Test: `McpServerTemplate.E2E.AuthorizationServersTests.T11_4_an_http_issuer_refuses_to_start`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:07:11Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `d5506b15ad6c`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Authentication:IdentityProviders:idp-b:Issuer=http://idp-b.e2e.test/ would send clients to a plaintext issuer, and the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-4-issuer-query-left-out`

- Test: `McpServerTemplate.E2E.AuthorizationServersTests.T11_4_an_issuer_with_a_query_refuses_to_start`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:07:56Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `d5506b15ad6c`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Authentication:IdentityProviders:idp-b:Issuer=https://idp-b.e2e.test/?tenant=e2e would be published as an authorization server with a query, and the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-3-keycloak-client-claim-left-as-azp`

- Test: `McpServerTemplate.E2E.ClientClaimTests.T11_3_a_token_without_the_claim_that_ClientIdClaim_names_is_refused`
- Acts on container environment: The class's server keeps Keycloak's ClientIdClaim as the environment has it, azp, the claim Keycloak's tokens carry.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.4 s
- Taken 2026-09-29T04:08:42Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `4d0782cafbcc`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : with ClientIdClaim=client_id, a Keycloak token carrying only azp got 200, not 401: the claim ClientIdClaim names was not the one required.
~~~

### `t11-3-client-claim-left-out.sub`

- Test: `McpServerTemplate.E2E.ClientClaimTests.T11_3_a_client_claim_that_is_always_present_or_means_something_else_refuses_to_start(claim: "sub")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:09:25Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `4d0782cafbcc`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Authentication:IdentityProviders:keycloak:ClientIdClaim=sub names a claim that would switch the client requirement off, and the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-3-client-claim-left-out.scope`

- Test: `McpServerTemplate.E2E.ClientClaimTests.T11_3_a_client_claim_that_is_always_present_or_means_something_else_refuses_to_start(claim: "scope")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:10:09Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `4d0782cafbcc`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Authentication:IdentityProviders:keycloak:ClientIdClaim=scope names a claim that would switch the client requirement off, and the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-3-client-claim-left-out.typ`

- Test: `McpServerTemplate.E2E.ClientClaimTests.T11_3_a_client_claim_that_is_always_present_or_means_something_else_refuses_to_start(claim: "typ")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:10:52Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `4d0782cafbcc`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Authentication:IdentityProviders:keycloak:ClientIdClaim=typ names a claim that would switch the client requirement off, and the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t17-key-written-in-the-profile`

- Test: `McpServerTemplate.E2E.DataProtectionTests.T17_the_image_writes_no_key_and_loads_no_key_ring`
- Acts on container environment: A key file is written into the class's server container at /home/app/.aspnet/DataProtection-Keys, where the framework wrote its unencrypted key before G-18, through Docker's archive API.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.1 s
- Taken 2026-09-29T04:11:35Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `821edadb8e83`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : the image wrote under /home/app/.aspnet: [Add /home/app/.aspnet, Add /home/app/.aspnet/DataProtection-Keys, Add /home/app/.aspnet/DataProtection-Keys/key-e2e-sabotage.xml]; its log said: []; GET /readyz answered 200.
~~~

### `t9-documented-resource-at-the-root`

- Test: `McpServerTemplate.E2E.DocsProfileTests.T9_the_documented_deployment_starts_accepts_a_real_token_and_lists_tools`
- Acts on inputs: The document the profile reads says Authentication__Resource=https://${MCP_HOST}/ where docs/04 says …/mcp, as it did before G-12 (1); the rest of it, and of the run, is unchanged.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 39.2 s
- Taken 2026-09-29T04:12:19Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `a91a6538a94e`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : the documented deployment did not start: the documented server (C:\Users\Windows\AppData\Local\Temp\mcp-e2e-20260929-041137-ade801\docs04-resource-at-the-root.md) exited with code 78; [04:12:15 FTL] : MCP Server cannot start: Authentication:Resource must name this server's MCP endpoint, https://{host}/mcp; it is 'https://mcp.e2e.test/', whose path is '/'. MCP answers at /mcp (with one trailing slash at most), and a client that connects to a resource URL where nothing answers cannot connect.
~~~

### `t11-1-resource-at-the-root-left-out`

- Test: `McpServerTemplate.E2E.EndpointAndResourceTests.T11_1_a_resource_whose_path_is_not_mcp_refuses_to_start`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:13:05Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `aba186f1fecb`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Authentication:Resource=https://mcp.e2e.test/ names a resource MCP does not answer at, and the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-1-dot-segment-left-out`

- Test: `McpServerTemplate.E2E.EndpointAndResourceTests.T11_1_a_resource_whose_path_is_mcp_only_once_parsed_refuses_to_start`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:13:53Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `aba186f1fecb`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Authentication:Resource=https://mcp.e2e.test/./mcp is published as written, and the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-1-client-dials-the-root`

- Test: `McpServerTemplate.E2E.EndpointAndResourceTests.T11_1_the_challenge_at_the_resource_url_and_the_rfc9728_location_answer_with_the_same_document`
- Acts on inputs: The unauthenticated initialize is sent to https://mcp.e2e.test/, the root, where MCP answered before G-12 (1), instead of the resource's URL.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.0 s
- Taken 2026-09-29T04:14:38Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `aba186f1fecb`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : an unauthenticated initialize to the resource URL https://mcp.e2e.test/ got 404 with no resource_metadata in its challenge, not a 401 naming its metadata.
~~~

### `t11-2-open-variant-left-out.no-allowed-hosts`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start(variant: "no-allowed-hosts", key: "HttpTransport:AllowedHosts", value: null)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:15:23Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with no AllowedHosts: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com got 400.
~~~

### `t11-2-open-variant-left-out.allowed-ipv4-any`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start(variant: "allowed-ipv4-any", key: "HttpTransport:AllowedHosts:0", value: "0.0.0.0")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:16:07Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=0.0.0.0: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com got 400.
~~~

### `t11-2-open-variant-left-out.allowed-star`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start(variant: "allowed-star", key: "HttpTransport:AllowedHosts:0", value: "*")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:16:53Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=*: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com got 400.
~~~

### `t11-2-open-variant-left-out.allowed-ipv6-any-bracketed`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start(variant: "allowed-ipv6-any-bracketed", key: "HttpTransport:AllowedHosts:0", value: "[::]")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:17:37Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=[::]: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com got 400.
~~~

### `t11-2-open-variant-left-out.allowed-ipv6-any`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_wildcard_bind_with_no_real_allowed_host_refuses_to_start(variant: "allowed-ipv6-any", key: "HttpTransport:AllowedHosts:0", value: "::")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:18:21Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=::: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com got 400.
~~~

### `t11-2-not-one-name-left-out.subdomain-wildcard-tld`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start(variant: "subdomain-wildcard-tld", value: "*.com", admits: "evil.com")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:19:09Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=*.com: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: evil.com got 400.
~~~

### `t11-2-not-one-name-left-out.wildcard-root-dot`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start(variant: "wildcard-root-dot", value: "*.", admits: "attacker.example.com.")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:19:53Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=*.: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com. got 400.
~~~

### `t11-2-not-one-name-left-out.subdomain-wildcard`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start(variant: "subdomain-wildcard", value: "*.example.com", admits: "attacker.example.com")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:20:38Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=*.example.com: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com got 400.
~~~

### `t11-2-not-one-name-left-out.fullwidth-star`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start(variant: "fullwidth-star", value: "＊", admits: "attacker.example.com")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:21:22Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=＊: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: attacker.example.com got 400.
~~~

### `t11-2-not-one-name-left-out.trailing-dot`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start(variant: "trailing-dot", value: "mcp.e2e.test.", admits: "mcp.e2e.test.")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:22:06Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=mcp.e2e.test.: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: mcp.e2e.test. got 400.
~~~

### `t11-2-not-one-name-left-out.unset-variable`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_allowed_host_that_is_not_one_name_as_written_refuses_to_start(variant: "unset-variable", value: "", admits: "mcp.e2e.test")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:22:51Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a 0.0.0.0 bind with HttpTransport:AllowedHosts:0=: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it with Host: mcp.e2e.test got 200.
~~~

### `t11-2-unservable-setting-left-out.bind-broadcast`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start(variant: "bind-broadcast", key: "HttpTransport:BindAddress", value: "255.255.255.255")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.9 s
- Taken 2026-09-29T04:23:36Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=255.255.255.255: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-unservable-setting-left-out.bind-multicast`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start(variant: "bind-multicast", key: "HttpTransport:BindAddress", value: "224.0.0.1")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:24:21Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=224.0.0.1: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-unservable-setting-left-out.proxy-not-an-address`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start(variant: "proxy-not-an-address", key: "HttpTransport:KnownProxies:0", value: "not-an-ip")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:25:05Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:KnownProxies:0=not-an-ip: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-unservable-setting-left-out.network-prefix-too-long`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_setting_the_image_cannot_serve_by_refuses_to_start(variant: "network-prefix-too-long", key: "HttpTransport:KnownNetworks:0", value: "10.0.0.0/99")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:25:50Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:KnownNetworks:0=10.0.0.0/99: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-unbindable-address-left-out.bind-link-local`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start(variant: "bind-link-local", bindAddress: "fe80::1")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:26:35Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=fe80::1: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-unbindable-address-left-out.bind-link-local-unknown-zone`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start(variant: "bind-link-local-unknown-zone", bindAddress: "fe80::1%nosuchnic")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:27:20Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=fe80::1%nosuchnic: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-unbindable-address-left-out.bind-not-held`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start(variant: "bind-not-held", bindAddress: "10.1.2.3")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:28:05Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=10.1.2.3: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-unbindable-address-left-out.bind-octal`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_bind_address_the_image_cannot_bind_refuses_to_start(variant: "bind-octal", bindAddress: "010.0.0.1")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:28:54Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=010.0.0.1: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-bind-path-left-out`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_bind_address_with_a_path_refuses_to_start`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:29:38Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=127.0.0.1/x: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-empty-bind-left-out`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_empty_bind_address_refuses_to_start_naming_the_key`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:30:22Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress= (empty): the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-ipv4-mapped-bind-left-out.ipv4-mapped-bracketed`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_ipv4_mapped_bind_address_refuses_to_start(variant: "ipv4-mapped-bracketed", bindAddress: "[::ffff:127.0.0.1]")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T04:31:07Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=[::ffff:127.0.0.1]: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-ipv4-mapped-bind-left-out.ipv4-mapped`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_an_ipv4_mapped_bind_address_refuses_to_start(variant: "ipv4-mapped", bindAddress: "::ffff:127.0.0.1")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:31:51Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : HttpTransport:BindAddress=::ffff:127.0.0.1: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t11-2-kestrel-endpoint-left-out`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_a_kestrel_setting_refuses_to_start`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T04:32:38Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a server whose BindAddress is loopback, with Kestrel:Endpoints:Web:Url=http://0.0.0.0:3001: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. A GET /healthz sent to it from off the machine with Host: localhost got 400.
~~~

### `t11-2-attacker-host-allowed`

- Test: `McpServerTemplate.E2E.HostFilteringTests.T11_2_once_a_real_name_is_set_a_foreign_host_is_refused`
- Acts on container environment: The class's server also allows the attacker's host name: HttpTransport:AllowedHosts:1 is attacker.example.com.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.0 s
- Taken 2026-09-29T04:33:22Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `cf1f86416d62`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : with AllowedHosts=mcp.e2e.test, a request with Host: attacker.example.com got 200, not 400.
~~~

### `t7-server-b-counts-in-the-environments-redis`

- Test: `McpServerTemplate.E2E.LimitsTests.T7_one_callers_count_carries_across_both_servers_while_other_callers_are_served`
- Acts on container environment: Server B counts in the environment's own Redis (Limits:Redis=redis.e2e.test:6379) instead of this class's, so the two servers count apart and the request after the limit is only server A's fourth.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.0 s
- Taken 2026-09-29T04:34:13Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `f5765f545a19`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : with Limits:PerPrincipalPerMinute=6 and both servers counting in redis-limits.e2e.test:6379, the caller's requests went [A: served; B: served; A: served; B: served; A: served; B: served], and its next, on server A — which had served 3 of them — got '{"result":{"contents":[{"uri":"smhi://coverage-area","mimeType":"text/plain","text":"SMHI Forecast Coverage Area\r\n===========================\r\n\r\nGeographic bounds (approximate):\r\n  Latitude:  50\u00B0N to 72\u00B0N\r\n  Longitude: -1\u00B0E to 40\u00B0E\r\n\r\nCovers:\r\n  - Sweden (full coverage)\r\n  - Norway (most areas)\r\n  - Finland (full coverage)\r\n  - Denmark (full coverage)\r\n  - Baltic states (Estonia, Latvia, Lithuania)\r\n  - Parts of northern Poland, Germany, and western Russia\r\n\r\nLimitations:\r\n  - Points over open ocean may not have data\r\n  - Resolution is approximately 2.5 km grid\r\n  - Forecast horizon: ~10 days (~70 hourly time steps for first ~3 days,\r\n    then 3-hourly, then 6-hourly)\r\n\r\nCoordinate examples:\r\n  Stockholm:   59.33\u00B0N, 18.07\u00B0E\r\n  Gothenburg:  57.71\u00B0N, 11.97\u00B0E\r\n  Malm\u00F6:       55.60\u00B0N, 13.00\u00B0E\r\n  Oslo:        59.91\u00B0N, 10.75\u00B0E\r\n  Helsinki:    60.17\u00B0N, 24.94\u00B0E\r\n  Copenhagen:  55.68\u00B0N, 12.57\u00B0E"}]},"id":1,"jsonrpc":"2.0"}', not a caller-rate refusal: the count did not carry from one server to the other.
~~~

### `t7-other-caller-minted-for-the-same-subject`

- Test: `McpServerTemplate.E2E.LimitsTests.T7_one_callers_count_carries_across_both_servers_while_other_callers_are_served`
- Acts on inputs: The other caller's token is minted at idp-a for the exhausted caller's own subject, so it is the same caller, not another.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.1 s
- Taken 2026-09-29T04:35:01Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `f5765f545a19`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : once e2e-limits-ef6d6bf2e562487db525c2a2a2cc4249 at idp-a.e2e.test was refused for its rate, another subject at idp-a.e2e.test got 'excess_rate_limit_exceeded (rule: caller-rate). You have made 6 requests in the last minute, which is your limit. Wait and try again.', and e2e-limits-ef6d6bf2e562487db525c2a2a2cc4249 at idp-b.e2e.test got served: each is another caller, with a count of its own, and is served.
~~~

### `t7-second-address-forwarded-as-the-first`

- Test: `McpServerTemplate.E2E.LimitsTests.T7_one_forwarded_address_gets_429_on_its_61st_request_while_another_gets_200`
- Acts on inputs: The second address's request is forwarded as the first address instead of a fresh one, so it shares the count the first has spent.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.2 s
- Taken 2026-09-29T04:35:47Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `f5765f545a19`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : the forwarded address 203.0.113.2 got 200 on each of its first 60 requests and 429 on its 61st; 203.0.113.3, through the same front just after, got 429. One address is throttled at 61 and another served only if the server counts by the X-Forwarded-For the front sets: if that header was lost, every request shares the front's own count, a fresh address is refused for what others spent, and it reads as the product's 429 (forwarded address not honoured).
~~~

### `t7-redis-left-running`

- Test: `McpServerTemplate.E2E.LimitsTests.T7_with_redis_stopped_requests_are_refused_limits_unavailable`
- Acts on container environment: The test's Redis is left running instead of being stopped.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 4.7 s
- Taken 2026-09-29T04:36:38Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `f5765f545a19`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : with the server's Redis (redis-outage.e2e.test:6379) stopped, reading smhi://coverage-area got served and calling get_forecast got served: each must be refused with limits-unavailable and nothing returned, never let through uncounted.
~~~

### `t11-5-sink-path-writable`

- Test: `McpServerTemplate.E2E.LogSinkTests.T11_5_a_file_sink_at_another_index_that_cannot_write_refuses_to_start_naming_the_resolved_path`
- Acts on container environment: The sink at Serilog:WriteTo:2 is pointed at logs/e2e-sabotage-.log, in the log directory the app user may write to (G-1), instead of a directory it cannot create.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 37.8 s
- Taken 2026-09-29T04:37:21Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `0314aa233869`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a File sink at Serilog:WriteTo:2 pointed at 'e2e-unwritable/e2e-.log' (resolved /app/e2e-unwritable/e2e-.log, which user 1654 cannot create): the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; its stderr never mentions the sink.
~~~

### `t11-5-variable-stays-inside-logs`

- Test: `McpServerTemplate.E2E.LogSinkTests.T11_5_a_file_sink_whose_path_climbs_out_through_an_environment_variable_refuses_to_start`
- Acts on container environment: MCP_LOGDIR is e2e instead of ../../tmp, so the sink's path expands to logs/e2e/e2e-.log, inside the log directory.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 37.7 s
- Taken 2026-09-29T04:38:04Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `0314aa233869`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a File sink at Serilog:WriteTo:2:Args:path='logs/%MCP_LOGDIR%/e2e-.log' with MCP_LOGDIR=../../tmp, which the sink resolves to /tmp/e2e-.log: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal.
~~~

### `settings-read-once-idp-b-keys-from-idp-a`

- Test: `McpServerTemplate.E2E.SettingsReadOnceTests.A_settings_file_changed_while_the_server_runs_changes_nothing_until_it_restarts`
- Acts on container environment: The server is started with idp-b's Authority at https://idp-a.e2e.test, so idp-b's scheme takes idp-a's keys from the start, as it would had the running server read the changed file.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 44.8 s
- Taken 2026-09-29T04:38:53Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `9bdfdd724cf1`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : after /app/appsettings.Production.json gained Authentication:Schemes:idp:idp-b:MetadataAddress=https://idp-a.e2e.test/…, a token naming idp-b and signed with idp-a's key got 200, not 401. The running server applied a setting no startup check had read.
~~~

### `t2-hidden-tool-and-unscoped-prompt-callers-given-weather-read`

- Test: `McpServerTemplate.E2E.ShippedImageRefusalTests.T2_contract_003s_refusals_each_carry_their_rule_return_nothing_and_reach_no_upstream`
- Acts on inputs: The hidden-tool and unscoped-prompt callers' token is minted with weather:read as well, so the tool runs and the prompt answers.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.3 s
- Taken 2026-09-29T04:39:37Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `17a81a500256`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a hidden tool got '{"result":{"content":[{"type":"text","text":"An error occurred invoking \u0027get_forecast\u0027: No forecast data available for coordinates (57.6289, 20.8301). The location may be outside SMHI coverage (Northern Europe, approx. 50-72N, -1-40E) or over water/outside the forecast grid."}],"isError":true},"id":1,"jsonrpc":"2.0"}', not a refusal by rule insufficient_scope with no content; an unscoped prompt got '{"result":{"description":"Generates a structured weather briefing for a location. Guides the assistant to provide a comprehensive yet concise weather summary including current conditions, upcoming forecast, and any weather alerts.","messages":[{"content":{"type":"text","text":"Please provide a weather briefing for coordinates (59.33, 18.07).\r\n\r\nFollow this structure:\r\n1. **Current Conditions** \u2014 Use GetCurrentWeather to get the current snapshot\r\n2. **Today\u0027s Outlook** \u2014 Summarize the rest of today from the forecast\r\n3. **Coming Days** \u2014 Use GetForecast to get the multi-day forecast, highlight key changes\r\n4. **Alerts** \u2014 Flag any notable weather: heavy precipitation, strong wind (\u003E15 m/s),\r\n   thunderstorms, extreme temperatures, or rapid changes\r\n\r\nKeep the briefing conversational and concise. Use the weather symbol descriptions\r\nfrom the smhi://weather-symbols resource if needed for context."},"role":"user"}]},"id":1,"jsonrpc":"2.0"}', not a refusal by rule not-permitted with no content; and the fake recorded 1 request(s) from this server after the permitted call: [GET /api/category/snow1g/version/1/geotype/point/lon/20.830100/lat/57.628900/data.json].
~~~

### `t4-weather-left-bound-to-keycloak`

- Test: `McpServerTemplate.E2E.StandardClientTests.T4_the_sdk_client_given_only_the_resource_url_discovers_authorizes_and_calls_a_tool`
- Acts on container environment: The class's server leaves the weather provider bound to Keycloak, as the environment has it, rather than to idp-a, the authorization server the client is sent to first: the client's token is from the other trust domain.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.8 s
- Taken 2026-09-29T04:40:20Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `acd2efee5b48`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Assert.DoesNotContain() Failure: Sub-string found
                                ↓ (pos 66)
String: ···"_info': authz_fail (rule: idp-binding). T"···
Found:  "rule:"
~~~

### `t10-credential-left-out.resource`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential(row: "resource", key: "Authentication:Resource", value: "https://ops:Pa55w0rd-e2e@mcp.e2e.test/mcp")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.7 s
- Taken 2026-09-29T14:52:10Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : resource, Authentication:Resource set to a URL carrying a password: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. The refusal must name the key, with exit 78, and never write the credential.
~~~

### `t10-credential-left-out.authority`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential(row: "authority", key: "Authentication:IdentityProviders:idp-a:Authority", value: "https://svc-reader:Pa55w0rd-e2e@idp-a.e2e.test")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 2.2 s
- Taken 2026-09-29T14:52:59Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : authority, Authentication:IdentityProviders:idp-a:Authority set to a URL carrying a password: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. The refusal must name the key, with exit 78, and never write the credential.
~~~

### `t10-credential-left-out.issuer`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential(row: "issuer", key: "Authentication:IdentityProviders:idp-a:Issuer", value: "https://svc-reader:Pa55w0rd-e2e@idp-a.e2e.test")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.9 s
- Taken 2026-09-29T14:54:01Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : issuer, Authentication:IdentityProviders:idp-a:Issuer set to a URL carrying a password: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. The refusal must name the key, with exit 78, and never write the credential.
~~~

### `t10-credential-left-out.base-url`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential(row: "base-url", key: "Providers:Smhi:BaseUrl", value: "http://svc-reader:Pa55w0rd-e2e@opendata-download-m"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T14:54:53Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : base-url, Providers:Smhi:BaseUrl set to a URL carrying a password: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. The refusal must name the key, with exit 78, and never write the credential.
~~~

### `t10-credential-left-out.authority-query`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential(row: "authority-query", key: "Authentication:IdentityProviders:idp-a:Authority", value: "https://idp-a.e2e.test/?client_secret=Pa55w0rd-e2e")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T14:55:43Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : authority-query, Authentication:IdentityProviders:idp-a:Authority set to a URL carrying a password: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. The refusal must name the key, with exit 78, and never write the credential.
~~~

### `t10-credential-left-out.base-url-query`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_url_carrying_a_credential_exits_78_without_writing_the_credential(row: "base-url-query", key: "Providers:Smhi:BaseUrl", value: "http://opendata-download-metfcst.smhi.se/?api_key="···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T14:56:29Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : base-url-query, Providers:Smhi:BaseUrl set to a URL carrying a password: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal. The refusal must name the key, with exit 78, and never write the credential.
~~~

### `t10-forwarded-headers-switch-left-out`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_the_frameworks_forwarded_headers_switch_exits_78_naming_where_it_came_from`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T14:57:14Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : under Staging with no proxy declared and ASPNETCORE_FORWARDEDHEADERS_ENABLED=true: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; 65 GET /healthz sent straight to it, each forwarded as an address of its own, got 59×200 and 6×429; the cause, 'ForwardedHeaders_Enabled is 'true' from the environment, as ASPNETCORE_FORWARDEDHEADERS_ENABLED', is not named with exit 78.
~~~

### `t10-staging-with-no-proxy-left-out`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_outside_development_a_server_with_no_proxy_it_trusts_exits_78`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 2.3 s
- Taken 2026-09-29T14:58:00Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : under Staging with no proxy declared: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; a valid bearer token sent to it over plain http got 200; the cause, 'must sit behind a proxy it trusts explicitly', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.unknown-setting`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "unknown-setting", key: "Limits:PerPrincipalPerMinit", value: "10", cause: "'Limits:PerPrincipalPerMinit' is not a setting thi"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T14:58:51Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : unknown-setting, Limits:PerPrincipalPerMinit=10: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, ''Limits:PerPrincipalPerMinit' is not a setting this server reads', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.production-without-redis`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "production-without-redis", key: "Limits:Redis", value: null, cause: "Limits:Redis is required outside Development")`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T14:59:37Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : production-without-redis, without Limits:Redis: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'Limits:Redis is required outside Development', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.http-authority`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "http-authority", key: "Authentication:IdentityProviders:idp-a:Authority", value: "http://idp-a.e2e.test", cause: "Authentication:IdentityProviders:idp-a:Authority m"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T15:00:25Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : http-authority, Authentication:IdentityProviders:idp-a:Authority=http://idp-a.e2e.test: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'Authentication:IdentityProviders:idp-a:Authority must be an absolute https URI', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.missing-allowed-hosts`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "missing-allowed-hosts", key: "HttpTransport:AllowedHosts", value: null, cause: "HttpTransport:AllowedHosts must name the host name"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T15:01:09Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : missing-allowed-hosts, without HttpTransport:AllowedHosts: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'HttpTransport:AllowedHosts must name the host names clients reach this server by', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.shutdown-timeout-set`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "shutdown-timeout-set", key: "DOTNET_shutdownTimeoutSeconds", value: "30", cause: "shutdownTimeoutSeconds is '30' from the environmen"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T15:01:56Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : shutdown-timeout-set, DOTNET_shutdownTimeoutSeconds=30: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'shutdownTimeoutSeconds is '30' from the environment, as DOTNET_shutdownTimeoutSeconds', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.known-network-of-every-address`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "known-network-of-every-address", key: "HttpTransport:KnownNetworks:0", value: "0.0.0.0/0", cause: "HttpTransport:KnownNetworks:0 is '0.0.0.0/0', whos"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T15:02:41Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : known-network-of-every-address, HttpTransport:KnownNetworks:0=0.0.0.0/0: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'HttpTransport:KnownNetworks:0 is '0.0.0.0/0', whose prefix length is 0', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.known-network-of-every-ipv6-address`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "known-network-of-every-ipv6-address", key: "HttpTransport:KnownNetworks:0", value: "::/0", cause: "HttpTransport:KnownNetworks:0 is '::/0', whose pre"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T15:03:26Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : known-network-of-every-ipv6-address, HttpTransport:KnownNetworks:0=::/0: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'HttpTransport:KnownNetworks:0 is '::/0', whose prefix length is 0', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.known-network-past-its-prefix`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "known-network-past-its-prefix", key: "HttpTransport:KnownNetworks:0", value: "10.213.99.250/2", cause: "HttpTransport:KnownNetworks:0 is '10.213.99.250/2'"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T15:04:13Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : known-network-past-its-prefix, HttpTransport:KnownNetworks:0=10.213.99.250/2: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'HttpTransport:KnownNetworks:0 is '10.213.99.250/2', whose address has bits set past its prefix length, so it would be read as 0.0.0.0/2', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.known-network-broader-than-a-slash-8`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "known-network-broader-than-a-slash-8", key: "HttpTransport:KnownNetworks:0", value: "128.0.0.0/1", cause: "HttpTransport:KnownNetworks:0 is '128.0.0.0/1', wh"···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T15:04:59Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : known-network-broader-than-a-slash-8, HttpTransport:KnownNetworks:0=128.0.0.0/1: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'HttpTransport:KnownNetworks:0 is '128.0.0.0/1', whose prefix length, 1, is broader than /8', is not named with exit 78.
~~~

### `t10-misconfiguration-left-out.two-identity-providers-one-issuer`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_misconfiguration_exits_78_naming_its_cause(row: "two-identity-providers-one-issuer", key: "Authentication:IdentityProviders:idp-b:Issuer", value: "https://idp-a.e2e.test", cause: "Authentication:IdentityProviders:idp-a:Issuer and "···)`
- Acts on container environment: The server is started without the misconfiguration it must refuse, on the environment's settings alone, and comes up.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.6 s
- Taken 2026-09-29T15:05:46Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : two-identity-providers-one-issuer, Authentication:IdentityProviders:idp-b:Issuer=https://idp-a.e2e.test: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal; the cause, 'Authentication:IdentityProviders:idp-a:Issuer and Authentication:IdentityProviders:idp-b:Issuer are both 'https://idp-a.e2e.test'', is not named with exit 78.
~~~

### `t10-stop-signal-swallowed`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_docker_stop_ends_the_server_cleanly_within_the_grace_with_a_shutdown_line`
- Acts on inputs: docker stop sends SIGWINCH, which the server does not handle, instead of the image's own stop signal, as an entrypoint that swallows SIGTERM would leave it: the server never begins to stop, and Docker kills it once the grace has passed.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 11.5 s
- Taken 2026-09-29T15:06:29Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : docker stop sent SIGWINCH and the server exited 137 after 10,3 s (grace 10 s), with no shutdown line; a clean stop is SIGTERM, exit 0 within Docker's 10-second grace, and a shutdown line. Its stderr ends: 2026-09-29T15:07:09.662209281Z [15:07:09 INF] : Starting MCP server with HTTP transport on 0.0.0.0:3001 | 2026-09-29T15:07:09.935622524Z [15:07:09 INF] McpServerTemplate.Infrastructure.Frame.RequestGate : Frame installed: limits=Redis providers=Smhi,SmhiObs requests=completion/complete,initialize,notifications/cancelled,notifications/initialized,ping,prompts/get,prompts/list,resources/list,resources/read,resources/templates/list,server/discover,tools/call,tools/list :: McpRequestFilters.CallToolFilters=[frame,frame,sdk] McpRequestFilters.CallToolWithAlternateFilters=[sdk] McpRequestFilters.CompleteFilters=[frame] McpRequestFilters.GetPromptFilters=[frame,sdk,sdk] McpRequestFilters.ListPromptsFilters=[frame,sdk,sdk] McpRequestFilters.ListResourceTemplatesFilters=[frame,sdk,sdk] McpRequestFilters.ListResourcesFilters=[frame,sdk,sdk] McpRequestFilters.ListToolsFilters=[frame,sdk,sdk] McpRequestFilters.ReadResourceFilters=[frame,sdk,sdk] McpMessageFilters.IncomingFilters=[frame] | prompt:Smhi/forecast_briefing:weather:read;resource:Smhi/smhi://coverage-area:weather:read;resource:Smhi/smhi://weather-symbols:weather:read;tool:Smhi/get_current_weather:weather:read:Read;tool:Smhi/get_forecast:weather:read:Read;tool:Smhi/get_forecast_model_info:weather:read:Read;tool:SmhiObs/get_monthly_climate:observations:read:Read;tool:SmhiObs/get_precipitation_history:observations:read:Read;tool:SmhiObs/get_recent_temperature:observations:read:Read;tool:SmhiObs/get_temperature_history:observations:read:Read | 2026-09-29T15:07:09.965832769Z [15:07:09 WRN] Microsoft.AspNetCore.Hosting.Diagnostics : Overriding HTTP_PORTS '8080' and HTTPS_PORTS ''. Binding to values defined by URLS instead 'http://0.0.0.0:3001'. | 2026-09-29T15:07:10.090331648Z [15:07:10 WRN] Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware : Failed to determine the https port for redirect.
~~~

### `t10-busy-stop-signal-swallowed`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_docker_stop_with_a_request_in_flight_ends_the_server_cleanly_within_the_grace`
- Acts on inputs: docker stop sends SIGWINCH, which the server does not handle, instead of the image's own stop signal, while the call is in flight: the busy server never begins to stop, and Docker kills it once the grace has passed.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 12.8 s
- Taken 2026-09-29T15:07:24Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : with a call in flight (the fake holding its upstream answer for 30 s), docker stop sent SIGWINCH and the server exited 137 after 10,3 s (grace 10 s), with no shutdown line; the call: no answer: HttpRequestException: Error while copying content to a stream.. A clean stop is SIGTERM, exit 0 within 8 seconds — the 6-second shutdown timeout and a 2-second margin, inside Docker's 10-second grace — and a shutdown line. Its stderr ends: 2026-09-29T15:08:19.839874859Z    at System.Net.Sockets.SocketAsyncEventArgs.TransferCompletionCallbackCore(Int32 bytesTransferred, Memory`1 socketAddress, SocketFlags receivedFlags, SocketError socketError) | 2026-09-29T15:08:19.839875640Z    at System.Threading.ThreadPoolWorkQueue.Dispatch() | 2026-09-29T15:08:19.839876308Z    at System.Threading.PortableThreadPool.WorkerThread.WorkerThreadStart() | 2026-09-29T15:08:19.839877025Z    at System.Threading.Thread.StartCallback() | 2026-09-29T15:08:19.839877712Z --- End of stack trace from previous location ---
~~~

### `t10-request-kind-sent-as-ping.resources-subscribe`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_request_kind_nobody_governs_is_refused_request_kind(method: "resources/subscribe")`
- Acts on inputs: The request whose answer the claim reads is sent as ping, a kind the image's startup line names, instead of the row's method; it is answered.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.7 s
- Taken 2026-09-29T15:08:24Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : resources/subscribe, a kind the image's startup line does not govern ([completion/complete, initialize, notifications/cancelled, notifications/initialized, ping, prompts/get, prompts/list, resources/list, resources/read, resources/templates/list, server/discover, tools/call, tools/list]), got {"result":{},"id":1,"jsonrpc":"2.0"}, not a request-kind refusal with no result.
~~~

### `t10-request-kind-sent-as-ping.logging-setlevel`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_request_kind_nobody_governs_is_refused_request_kind(method: "logging/setLevel")`
- Acts on inputs: The request whose answer the claim reads is sent as ping, a kind the image's startup line names, instead of the row's method; it is answered.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.5 s
- Taken 2026-09-29T15:09:12Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : logging/setLevel, a kind the image's startup line does not govern ([completion/complete, initialize, notifications/cancelled, notifications/initialized, ping, prompts/get, prompts/list, resources/list, resources/read, resources/templates/list, server/discover, tools/call, tools/list]), got {"result":{},"id":1,"jsonrpc":"2.0"}, not a request-kind refusal with no result.
~~~

### `t10-request-kind-sent-as-ping.e2e-no-such-method`

- Test: `McpServerTemplate.E2E.StartupAndShutdownTests.T10_a_request_kind_nobody_governs_is_refused_request_kind(method: "e2e/no-such-method")`
- Acts on inputs: The request whose answer the claim reads is sent as ping, a kind the image's startup line names, instead of the row's method; it is answered.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.5 s
- Taken 2026-09-29T15:09:56Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `545dca84e634`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : e2e/no-such-method, a kind the image's startup line does not govern ([completion/complete, initialize, notifications/cancelled, notifications/initialized, ping, prompts/get, prompts/list, resources/list, resources/read, resources/templates/list, server/discover, tools/call, tools/list]), got {"result":{},"id":1,"jsonrpc":"2.0"}, not a request-kind refusal with no result.
~~~

### `t8-expired-attempt-sent-before-it-expires`

- Test: `McpServerTemplate.E2E.TestHostConfirmationTests.T8_five_tampering_attempts_are_each_refused_for_their_own_reason_and_the_tool_ran_once`
- Acts on inputs: The last attempt waits 100 seconds instead of 121, so the confirmation it sends has not expired: the tool runs a second time.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 101.4 s
- Taken 2026-09-29T04:49:51Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `601b1c7d72b1`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : an expired one got '{"result":{"content":[{"type":"text","text":"acted on e2e-0704c18aec9b42c296121c1b46959bf7 (run ad6ab1898fdc46cc835c185495adcf63)"}],"resultType":"complete","_meta":{"io.modelcontextprotocol/serverInfo":{"name":"McpServerTemplate","version":"1.0.0"}}},"id":3,"jsonrpc":"2.0"}', not a refusal by rule confirmation saying 'it is valid for 120', with no content; the witness shows 2 run(s): [e2e-0704c18aec9b42c296121c1b46959bf7/df51cad800a04e488776c884a21dd67c, e2e-0704c18aec9b42c296121c1b46959bf7/ad6ab1898fdc46cc835c185495adcf63], not exactly one.
~~~

### `t8-unscoped-caller-given-demo-read`

- Test: `McpServerTemplate.E2E.TestHostTests.T8_an_unscoped_completion_is_refused_not_permitted_with_no_result_and_its_refusal_is_on_stderr`
- Acts on inputs: The unscoped caller's token is minted with demo:read as well, the prompt's scope, so it may be given completions.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 5.8 s
- Taken 2026-09-29T04:50:38Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `1f45580db716`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : an unscoped completion of e2e_city_briefing answered {"result":{"completion":{"values":["Ume\u00E5","Uppsala"],"total":2,"hasMore":false}},"id":1,"jsonrpc":"2.0"}, not a not-permitted refusal with no result.
~~~

### `t8-authority-under-test.production`

- Test: `McpServerTemplate.E2E.TestHostTests.T8_the_test_host_exits_78_given_an_identity_provider_outside_test(environmentName: "Production")`
- Acts on container environment: The Authority the test host is given is https://idp-a.example.test, a host under .test, instead of https://idp-a.example.com.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 3.2 s
- Taken 2026-09-29T04:51:21Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `1f45580db716`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : under Production, the test host given Authentication:IdentityProviders:idp-a:Authority=https://idp-a.example.test, a host not under .test: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t8-authority-under-test.staging`

- Test: `McpServerTemplate.E2E.TestHostTests.T8_the_test_host_exits_78_given_an_identity_provider_outside_test(environmentName: "Staging")`
- Acts on container environment: The Authority the test host is given is https://idp-a.example.test, a host under .test, instead of https://idp-a.example.com.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 3.1 s
- Taken 2026-09-29T04:52:05Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `1f45580db716`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : under Staging, the test host given Authentication:IdentityProviders:idp-a:Authority=https://idp-a.example.test, a host not under .test: the server started, and /readyz answered 200 under Host mcp.e2e.test; its stderr names no refusal
~~~

### `t8-test-host-also-serves-jsonplaceholder`

- Test: `McpServerTemplate.E2E.TestHostTests.T8_the_test_hosts_frame_line_is_the_shipped_images_but_for_the_test_modules`
- Acts on container environment: The class's test host also enables JsonPlaceholder (Providers:Enabled:4), a built-in provider that is not a test module and that the shipped image does not serve.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.8 s
- Taken 2026-09-29T04:52:48Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `1f45580db716`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : the test host's frame line is not the shipped image's plus its test modules. limits: Redis and Redis; providers added: [JsonPlaceholder, E2EIrreversible, E2ECompletions] (expected [E2EIrreversible, E2ECompletions]); manifest lines added: [prompt:E2ECompletions/e2e_city_briefing:demo:read; tool:E2EIrreversible/e2e_irreversible_act:demo:write:Irreversible; tool:JsonPlaceholder/add_post_comment:demo:write:Write; tool:JsonPlaceholder/create_blog_post:demo:write:Write; tool:JsonPlaceholder/create_user_todo:demo:write:Write; tool:JsonPlaceholder/get_blog_post:demo:read:Read; tool:JsonPlaceholder/get_post_comments:demo:read:Read; tool:JsonPlaceholder/get_user_todos:demo:read:Read]; manifest lines gone: []; filters equal.
~~~

### `t3-no-token-sent-straight-to-the-server`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_no_token_is_challenged_with_the_metadata_on_the_front`
- Acts on network: The request goes straight to the server's published port instead of through the front, so no forwarded scheme reaches the server from a proxy it trusts.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.0 s
- Taken 2026-09-29T13:55:54Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a request with no token got 401 with resource_metadata=http://mcp.e2e.test/.well-known/oauth-protected-resource/mcp, not a 401 naming metadata on https://mcp.e2e.test/.
~~~

### `t3-bad-token-minted-valid.wrong-audience`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "wrong-audience", claim: null, reason: "IDX10214")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.5 s
- Taken 2026-09-29T13:57:08Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a wrong-audience token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.expired`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "expired", claim: null, reason: "IDX10223")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 5.8 s
- Taken 2026-09-29T13:58:20Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a expired token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.alg-none`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "alg-none", claim: null, reason: "IDX10504")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.5 s
- Taken 2026-09-29T13:59:32Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a alg-none token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.hs256`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "hs256", claim: null, reason: "IDX10517")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.1 s
- Taken 2026-09-29T14:00:45Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a hs256 token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.cross-signed`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "cross-signed", claim: null, reason: "IDX10503")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.5 s
- Taken 2026-09-29T14:01:54Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a cross-signed token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.missing-claim-sub`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "missing-claim", claim: "sub", reason: "missing sub,")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 5.9 s
- Taken 2026-09-29T14:03:05Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a missing-claim sub token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.missing-claim-jti`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "missing-claim", claim: "jti", reason: "missing jti,")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.4 s
- Taken 2026-09-29T14:04:16Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a missing-claim jti token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.missing-claim-client-id`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "missing-claim", claim: "client_id", reason: "missing client_id (the client claim)")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 5.9 s
- Taken 2026-09-29T14:05:29Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a missing-claim client_id token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-bad-token-minted-valid.missing-claim-iat`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_bad_token_is_refused_with_its_reason_in_the_servers_log(kind: "missing-claim", claim: "iat", reason: "missing iat,")`
- Acts on inputs: The row's token is minted as kind valid instead of its own, wrong in no way.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 5.7 s
- Taken 2026-09-29T14:06:40Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a missing-claim iat token from idp-a.e2e.test got 200, not 401.
~~~

### `t3-unattributable-token-sent-valid.unreadable`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_token_the_server_cannot_attribute_is_refused_with_its_reason_in_its_log(kind: "unreadable", reason: "the token cannot be read as a JSON web token")`
- Acts on inputs: The row's token is idp-a's valid token instead, which the server attributes and accepts.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.0 s
- Taken 2026-09-29T14:07:46Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : an unreadable token got 200, and the server's log has no authn_login_fail line for it; a refusal with 401 and its reason ('the token cannot be read as a JSON web token') is what it should be.
~~~

### `t3-unattributable-token-sent-valid.oversized`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_token_the_server_cannot_attribute_is_refused_with_its_reason_in_its_log(kind: "oversized", reason: "the token is 9249 characters long, more than the 8"···)`
- Acts on inputs: The row's token is idp-a's valid token instead, which the server attributes and accepts.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.1 s
- Taken 2026-09-29T14:08:57Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : an oversized token got 200, and the server's log has no authn_login_fail line for it; a refusal with 401 and its reason ('the token is 9249 characters long, more than the 8192 this server reads') is what it should be.
~~~

### `t3-unattributable-token-sent-valid.no-issuer`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_token_the_server_cannot_attribute_is_refused_with_its_reason_in_its_log(kind: "no-issuer", reason: "the token names no issuer")`
- Acts on inputs: The row's token is idp-a's valid token instead, which the server attributes and accepts.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.0 s
- Taken 2026-09-29T14:10:05Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a no-issuer token got 200, and the server's log has no authn_login_fail line for it; a refusal with 401 and its reason ('the token names no issuer') is what it should be.
~~~

### `t3-key-confusion-token-minted-valid`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_key_confusion_token_is_refused_and_its_refusal_logged`
- Acts on inputs: The token is minted as kind valid instead: idp-a's own token, RS256, every claim valid.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 6.0 s
- Taken 2026-09-29T14:11:13Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : an HS256 token keyed with idp-a.e2e.test's own public key, under its real key id, got 200, and the server's log has no authn_login_fail line for it.
~~~

### `t3-stale-token-issued-a-minute-ago`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_a_write_tool_called_with_a_token_issued_six_minutes_ago_is_refused_for_its_age`
- Acts on inputs: The stale token is minted as issued 60 seconds ago instead of 360, inside the write gate's five minutes.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.2 s
- Taken 2026-09-29T14:12:23Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : create_user_todo called with a token issued 61 s ago answered 'An error occurred invoking 'create_user_todo': the new todo does not exist upstream. Do not retry; check the id. JSONPlaceholder serves posts 1-100 and users 1-10.', not a token-age refusal with no content.
~~~

### `t3-stranger-registered-on-the-server`

- Test: `McpServerTemplate.E2E.TokenRefusalTests.T3_an_unregistered_issuers_token_costs_no_key_lookup_at_any_issuer`
- Acts on container environment: The test's server also registers the stranger as an identity provider (Authentication:IdentityProviders:stranger, Authority and Issuer https://stranger.e2e.test, as the issuer registry registers every provider), so its token is routed to a scheme of its own, which fetches keys.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 4.7 s
- Taken 2026-09-29T14:13:28Z at f40d8318afed, with uncommitted changes, on Windows with Docker 29.5.3; test file `33fbb5bb5932`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a token from https://stranger.e2e.test, which no server is configured with, made this server ask the issuers for {"idp-a.e2e.test":{},"idp-b.e2e.test":{},"stranger.e2e.test":{"/.well-known/openid-configuration":1,"/jwks":1}}; every issuer name (idp-a.e2e.test, idp-b.e2e.test, stranger.e2e.test) should show no discovery and no key-set request.
~~~

### `t5-other-caller-minted-at-idp-a`

- Test: `McpServerTemplate.E2E.TrustDomainTests.T5_a_caller_from_the_other_issuer_sees_none_of_the_bound_providers_items_in_the_lists`
- Acts on inputs: The other-issuer caller's token is minted at idp-a.e2e.test instead: the bound providers' own caller.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.7 s
- Taken 2026-09-29T05:03:54Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `67939ef2a0b6`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a caller from idp-b.e2e.test, the other issuer, sees the bound providers' prompt forecast_briefing, resource smhi://coverage-area, resource smhi://weather-symbols, tool get_current_weather, tool get_forecast, tool get_forecast_model_info, tool get_monthly_climate, tool get_precipitation_history, tool get_recent_temperature, tool get_temperature_history in the lists.
~~~

### `t5-caller-minted-at-idp-a.resource`

- Test: `McpServerTemplate.E2E.TrustDomainTests.T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist(kind: "resource")`
- Acts on inputs: The caller's token is minted at idp-a.e2e.test instead, so the items are its own: a resource is read, and a tool or a prompt goes on to its arguments.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.6 s
- Taken 2026-09-29T05:04:38Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `67939ef2a0b6`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a caller from idp-b.e2e.test, the other issuer, is refused the bound providers' resources in other words than a resource that does not exist ('authz_fail (rule: not-permitted). You may not use the resource 'smhi://e2e-no-such-a300ffc78865495fb9609fd8a4d281b8', or it does not exist.'): 'smhi://coverage-area' got '{"result":{"contents":[{"uri":"smhi://coverage-area","mimeType":"text/plain","text":"SMHI Forecast Coverage Area\r\n===========================\r\n\r\nGeographic bounds (approximate):\r\n  Latitude:  50\u00B0N to 72\u00B0N\r\n  Longitude: -1\u00B0E to 40\u00B0E\r\n\r\nCovers:\r\n  - Sweden (full coverage)\r\n  - Norway (most areas)\r\n  - Finland (full coverage)\r\n  - Denmark (full coverage)\r\n  - Baltic states (Estonia, Latvia, Lithuania)\r\n  - Parts of northern Poland, Germany, and western Russia\r\n\r\nLimitations:\r\n  - Points over open ocean may not have data\r\n  - Resolution is approximately 2.5 km grid\r\n  - Forecast horizon: ~10 days (~70 hourly time steps for first ~3 days,\r\n    then 3-hourly, then 6-hourly)\r\n\r\nCoordinate examples:\r\n  Stockholm:   59.33\u00B0N, 18.07\u00B0E\r\n  Gothenburg:  57.71\u00B0N, 11.97\u00B0E\r\n  Malm\u00F6:       55.60\u00B0N, 13.00\u00B0E\r\n  Oslo:        59.91\u00B0N, 10.75\u00B0E\r\n  Helsinki:    60.17\u00B0N, 24.94\u00B0E\r\n  Copenhagen:  55.68\u00B0N, 12.57\u00B0E"}]},"id":1,"jsonrpc":"2.0"}'; 'smhi://weather-symbols' got '{"result":{"contents":[{"uri":"smhi://weather-symbols","mimeType":"text/plain","text":"SMHI Weather Symbol Codes\n========================\n\n   1: Clear sky\n   2: Nearly clear sky\n   3: Variable cloudiness\n   4: Halfclear sky\n   5: Cloudy sky\n   6: Overcast\n   7: Fog\n   8: Light rain showers\n   9: Moderate rain showers\n  10: Heavy rain showers\n  11: Thunderstorm\n  12: Light sleet showers\n  13: Moderate sleet showers\n  14: Heavy sleet showers\n  15: Light snow showers\n  16: Moderate snow showers\n  17: Heavy snow showers\n  18: Light rain\n  19: Moderate rain\n  20: Heavy rain\n  21: Thunder\n  22: Light sleet\n  23: Moderate sleet\n  24: Heavy sleet\n  25: Light snowfall\n  26: Moderate snowfall\n  27: Heavy snowfall\n\nThese codes appear as the \u0027Wsymb2\u0027 parameter in SMHI forecast data.\n"}]},"id":1,"jsonrpc":"2.0"}'.
~~~

### `t5-caller-minted-at-idp-a.prompt`

- Test: `McpServerTemplate.E2E.TrustDomainTests.T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist(kind: "prompt")`
- Acts on inputs: The caller's token is minted at idp-a.e2e.test instead, so the items are its own: a resource is read, and a tool or a prompt goes on to its arguments.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.7 s
- Taken 2026-09-29T05:05:21Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `67939ef2a0b6`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a caller from idp-b.e2e.test, the other issuer, is refused the bound providers' prompts in other words than a prompt that does not exist ('authz_fail (rule: not-permitted). You may not use the prompt 'e2e_no_such_73ae1e4907e04074901a6afa0f517a0f', or it does not exist.'): 'forecast_briefing' got 'An error occurred.'.
~~~

### `t5-caller-minted-at-idp-a.tool`

- Test: `TrustDomainTests.T5_using_a_bound_providers_item_from_the_other_issuer_is_refused_in_the_words_for_one_that_does_not_exist("tool")`
- Acts on inputs: The caller's token is minted at idp-a.e2e.test instead, so the items are its own: a resource is read, and a tool or a prompt goes on to its arguments.
- Held: the test is skipped by decision, so no red can be taken. Stopped for the pioneer: contract-005 T-5 (the words for a tool that does not exist) conflicts with contract-002 T-6 and the roadmap (rule idp-binding). Red recorded; resolve by decision, then remove this Skip.

### `t15-observations-host-left-to-the-fake`

- Test: `McpServerTemplate.E2E.UpstreamExtensionPointTests.T15_a_stand_in_registered_for_a_provider_host_receives_the_servers_call`
- Acts on network: The observations host is left registered to the fake: the run's upstream registry hands it to no stand-in, so its name reaches WireMock and no stand-in is started.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 1.5 s
- Taken 2026-09-29T05:06:05Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `50ec9f563473`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : the server's call to opendata-download-metobs.smhi.se was made with no stand-in started: nothing registered one for the host, and by the fake it replaced 1 time(s) [GET /api/version/1.0/parameter/1.json]: a host registered to the stand-in is answered by the stand-in, and only by it.
~~~

### `t1-keycloak-issuer-pinned-to-another-realm`

- Test: `McpServerTemplate.E2E.WalkingSkeletonTests.T1_a_keycloak_token_lists_tools_through_the_front`
- Acts on container environment: The class's server pins Keycloak's issuer to another realm of the same Keycloak (Authentication:IdentityProviders:keycloak:Issuer …/realms/other), so it accepts no token Keycloak's realm mcp issues.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 2.4 s
- Taken 2026-09-29T05:06:49Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `8499492da996`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : tools/list with a Keycloak token failed: HttpRequestException: Response status code does not indicate success: 401 (Unauthorized). The server's latest authn_login_fail line: (none on its stderr)
~~~

### `t1-idp-a-reached-by-another-name`

- Test: `McpServerTemplate.E2E.WalkingSkeletonTests.T1_the_test_issuer_is_one_issuer_by_one_name_from_the_server_and_from_the_test`
- Acts on container environment: The class's server reaches idp-a by another name: its Authentication:IdentityProviders:idp-a:Authority is https://stranger.e2e.test, a name of the same test issuer container that no server is configured with.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.8 s
- Taken 2026-09-29T05:07:31Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `8499492da996`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : the server (198.51.100.37) fetched no discovery document from idp-a.e2e.test; counts {"idp-a.e2e.test":{},"idp-b.e2e.test":{},"stranger.e2e.test":{"/.well-known/openid-configuration":2,"/jwks":2}}; and it refused idp-a.e2e.test's token (HttpRequestException: Response status code does not indicate success: 401 (Unauthorized).)
~~~

### `t1-authorization-server-outside-the-environment`

- Test: `McpServerTemplate.E2E.WalkingSkeletonTests.T1_the_sdk_clients_traffic_and_its_oauth_discovery_go_through_the_name_map`
- Acts on container environment: The class's server names an authorization server outside the environment first: idp-a's Issuer, which the protected-resource metadata lists, is https://idp-a.example.com, a name the test's name map does not hold.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.2 s
- Taken 2026-09-29T05:08:11Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `8499492da996`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : Assert.Contains() Failure: Filter not matched in collection
Collection: [Tuple (POST, https://mcp.e2e.test/mcp, 401), Tuple (GET, https://mcp.e2e.test/.well-known/oauth-protected-resource/mcp, 200), Tuple (GET, https://idp-a.example.com/.well-known/oauth-authorization-server, 0), Tuple (GET, https://idp-a.example.com/.well-known/openid-configuration, 0)]
~~~

### `t1-second-address-forwarded-as-the-first`

- Test: `McpServerTemplate.E2E.WalkingSkeletonTests.T1_the_fronts_forwarded_address_is_honoured`
- Acts on inputs: The second address's request is forwarded as the first address instead, so it shares the count the first has spent.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.2 s
- Taken 2026-09-29T05:08:56Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `8499492da996`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : forwarded address not honoured: 203.0.113.3 got 429 just after 203.0.113.2 spent its 60, so the server counted both as one address — the front's own, not the ones it forwarded.
~~~

### `t1-gateway-trusted-as-a-proxy`

- Test: `McpServerTemplate.E2E.WalkingSkeletonTests.T1_forwarded_headers_sent_straight_to_the_server_are_ignored`
- Acts on container environment: The class's server also trusts the run network's gateway (198.51.100.1), the address a request to a published port arrives from, as a proxy: HttpTransport:KnownProxies:1, beside the front.
- Result: red on its claim, with `McpServerTemplate.E2E.Harness.ClaimException` after 0.0 s
- Taken 2026-09-29T05:09:37Z at 53a5f8d23feb, with uncommitted changes, on Windows with Docker 29.5.3; test file `8499492da996`

~~~text
McpServerTemplate.E2E.Harness.ClaimException : a forged X-Forwarded-Proto from outside KnownProxies was trusted: the challenge to a request sent straight to the server is 'Bearer resource_metadata="https://mcp.e2e.test/.well-known/oauth-protected-resource/mcp"', not an http URL on mcp.e2e.test.
~~~
