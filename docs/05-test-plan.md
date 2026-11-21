# Test plan and results: Production & Inventory Tracker API

**Verdict: PASS** as of 2026-11-21, round 1 (re-test after the BUG-001 fix)

Open bugs: 0 blocker, 0 major, 0 minor (BUG-001 closed in round 1)

Scope (decision #111, owner): focused QA. Every PRD acceptance criterion and the section 5 non-functional requirements are mapped to the existing automated tests that prove them, after reading each test to confirm it checks the criterion and not only names it. New acceptance tests close the gaps found. A time-boxed exploratory pass ran against the real app. Round 1 re-tested the BUG-001 fix (decision #116) and the related password-length fix (decision #118), then ran the whole suite again. Paths of test files below are relative to `tests/InventoryTracker.Api.Tests/`.

Coverage status in section 2 is **before** QA's work: **Covered** (the existing tests prove the whole criterion), **Partly** (what is missing is said), **Not covered**. The Result column is **after** QA's work, from the runs in section 7.

## 1. How the product was run

Following `docs/03-architecture.md` section 8, with two deliberate departures the delivery manager asked for: throwaway secrets as environment variables instead of the owner's `dotnet user-secrets`, and a database in the QA scratchpad instead of `src/InventoryTracker.Api/inventory.db`.

```
dotnet build --artifacts-path TestResults/qa-art                      # 0 warnings, 0 errors
dotnet test  --artifacts-path TestResults/qa-art                      # baseline before QA's tests: 1,626 of 1,626 passed

# exploratory runs (bash syntax; in PowerShell set the same names with $env:NAME = "...")
ADMIN_EMAIL=qa.admin@example.test ADMIN_PASSWORD=<throwaway> JWT_SIGNING_KEY=<throwaway 46 chars> \
ConnectionStrings__Default="Data Source=<scratchpad>/explore.db" dotnet run --project src/InventoryTracker.Api
curl http://localhost:5133/health                                     # Healthy
```

All worked as written. Start-up migrated the empty database, created the first Manager from the variables and listened on `http://localhost:5133` in Development. The repository's own `inventory.db` was never created. A second run on a fresh database executed every request of `src/InventoryTracker.Api/InventoryTracker.Api.http` top to bottom, as a user clicking "Send Request" would (a small QA runner resolved its variables, `{{$dotenv}}` values and `{{name.response.body.$...}}` references): **58 requests, all 2xx**.

Environment: Windows 11 Home 10.0.26300, .NET SDK 10.0.401, xUnit v3 on Microsoft.Testing.Platform, Python 3.14 `urllib` scripts for the exploratory HTTP calls (no browser: the product has no UI).

## 2. Acceptance criteria

Abbreviations: `I/` = `Integration/`, `S/` = `Integration/Scenarios/`, `U/` = `Unit/`, `A/` = `Acceptance/` (added by QA). "+n" = n more tests tagged with the criterion in their comments (lists from a QA index of all 659 test methods by the criterion ids in their doc comments).

### US-01 to US-05: access and audit

| Criterion | Tests that prove it | Before QA | Result | Notes |
|-----------|--------------------|-----------|--------|-------|
| AC-01.1 | `I/AuthTests` `Sign_in_returns_token_expiry_email_and_roles`, `Token_from_sign_in_is_accepted` | Covered | pass | |
| AC-01.2 | `I/AuthTests` `Token_expires_after_exactly_8_hours` (fake clock, no skew) | Covered | pass | |
| AC-01.3 | `I/AuthTests` `Refused_sign_in_gives_invalid_credentials` (wrong password, unknown email, inactive user: same body) | Covered | pass | |
| AC-01.4 | `I/AuthTests` `Five_failures_lock_the_account_for_15_minutes`, `Simultaneous_wrong_passwords_still_lock_the_account`, `A_successful_sign_in_resets_the_failure_count`; +3 in `I/UsersTests` | Covered | pass | |
| AC-01.5 | `I/AuthTests` `Missing_or_bad_token_gives_401` (11 token faults), `Only_sign_in_and_health_allow_anonymous_callers`; `S/RoleMatrixTests` `Endpoint_without_a_token` (all 48 endpoints); `I/ErrorHandlingTests` `Unauthenticated_unknown_route_or_method_gives_401`; +18. Added: `A/UnknownPathAcceptanceTests` (14 more unknown paths and methods) | Covered | pass | `/health` and Development-only `/openapi/v1.json` are anonymous by owner decision #33, an agreed exception to the AC's wording. |
| AC-01.6 | `I/UsersTests` `Creating_a_user_without_a_token_gives_401_and_creates_nothing`; `I/AuthTests` `Only_sign_in_and_health_allow_anonymous_callers` | Covered | pass | |
| AC-02.1 | `I/StartupTests` `Empty_database_gets_one_active_manager_who_can_sign_in` | Covered | pass | Also seen live. |
| AC-02.2 | `I/StartupTests` `Restart_with_existing_users_changes_no_user` | Covered | pass | |
| AC-02.3 | `I/StartupTests` `Missing_admin_variable_stops_start_up`, `Admin_variable_messages_match_the_design` | Covered | pass | |
| AC-02.4 | `I/StartupTests` `Weak_admin_password_stops_start_up` (round 1: also 6 characters with emoji), `Admin_password_at_the_policy_minimum_is_accepted`, `Admin_password_of_8_characters_with_emoji_is_accepted` (round 1) | Covered | pass | The minimum has been counted in characters since decision #118. |
| AC-02.5 | Added: `A/SecretsConfigurationAcceptanceTests` `AC_02_5_env_example_lists_every_secret_with_a_placeholder_value`, `AC_02_5_committed_settings_files_hold_no_secret`, `AC_02_5_sample_requests_read_the_admin_credentials_from_a_git_ignored_file` | **Not covered** | pass | No test read `.env.example` or the committed settings. |
| AC-03.1 | `I/UsersTests` `Manager_creates_a_user_who_can_sign_in_with_exactly_those_roles` | Covered | pass | |
| AC-03.2 | `I/UsersTests` `Duplicate_email_ignoring_case_and_spaces_gives_409`, `Simultaneous_creates_for_one_email_give_one_201_and_one_409` | Covered | pass | |
| AC-03.3 | `I/UsersTests` `Invalid_create_input_gives_400_validation`, `Weak_initial_password_gives_400_weak_password` (round 1: also 6 characters with emoji), `Initial_password_of_8_characters_with_emoji_is_accepted` (round 1), `Invalid_role_change_gives_400_and_changes_nothing`; `I/TextLengthLimitTests` `Email_of_256_characters_with_emoji_is_accepted_and_257_is_refused` (round 1); `I/PasswordLengthValidatorTests` (round 1) | Covered | pass | |
| AC-03.4 | `I/UsersTests` `Manager_lists_users_newest_first_with_email_roles_and_status`, `No_user_response_contains_a_password_or_hash` | Covered | pass | |
| AC-03.5 | `I/UsersTests` `Adding_a_role_applies_from_the_next_sign_in`, `Removing_a_role_leaves_the_old_token_working_with_its_old_roles`; +3 | Covered | pass | |
| AC-03.6 | `I/UsersTests` `Deactivation_ends_existing_tokens_and_reactivation_allows_sign_in_again`, `Users_cannot_be_deleted`; `I/AuthTests` `Token_of_a_deactivated_user_gives_401`. Added: `A/UserRecordsAcceptanceTests` `AC_03_6_deactivated_users_still_show_on_the_movements_and_orders_they_touched` | **Partly**: nothing checked that a deactivated user's name stays on their movements and orders | pass | |
| AC-03.7 | `I/UsersTests` `Manager_sets_a_new_password_for_a_user`, `Manager_setting_an_invalid_password_gives_400` (round 1: also 6 characters with emoji), `A_Manager_reset_clears_the_lock_out` | Covered | pass | |
| AC-03.8 | `I/UsersTests` `The_last_active_Manager_cannot_lose_the_role_or_be_deactivated`, `An_inactive_Manager_does_not_count_as_another_Manager`, `Simultaneous_deactivations_cannot_remove_the_last_active_Manager`; +2 | Covered | pass | |
| AC-03.9 | `I/UsersTests` `Every_user_endpoint_gives_403_without_the_Manager_role` (Warehouse, Production, both), `Role_check_comes_before_the_unknown_id_check`; `S/RoleMatrixTests` (change-password open to all) | Covered | pass | |
| AC-04.1 | `I/UsersTests` `Changing_own_password_replaces_it_and_ends_existing_tokens`, `Invalid_change_password_input_gives_400` (round 1: also 6 characters with emoji) | Covered | pass | |
| AC-04.2 | `I/UsersTests` `Wrong_current_password_gives_400_and_changes_nothing` | Covered | pass | |
| AC-05.1 | `S/RoleMatrixTests` `Caller_gets_the_allowed_result_or_403` (every endpoint x Warehouse, Production, Manager, Warehouse+Production, success status on a real record or the exact 403), `Table_lists_every_endpoint_the_app_serves_with_its_roles`; +20 per feature | Covered | pass | |
| AC-05.2 | `S/RoleMatrixTests` (Warehouse+Production caller); `I/SendOrderTests` `User_with_warehouse_and_production_roles_can_send`; `I/NotificationsTests` `A_Manager_with_other_roles_may_use_every_endpoint`; +8 | Covered | pass | |
| AC-05.3 | User and UTC time on every record type: `I/ReceiptsTests`, `I/AdjustmentsTests`, `I/SendOrderTests` (movements); `I/ProductionOrdersTests`, `I/CompleteOrderTests`, `I/CancelOrderTests` (order history); `I/ComplementaryOrdersTests`, `I/ComplementarySendTests` (complementary history); `I/ReturnsTests` (return history); `I/StockTests` `Ledger_refuses_a_movement_without_user_and_utc_time`; 19 in all | Covered | pass | |

### US-06 to US-12: master data and stock

| Criterion | Tests that prove it | Before QA | Result | Notes |
|-----------|--------------------|-----------|--------|-------|
| AC-06.1 | `I/UnitsTests` `Manager_creates_a_unit_with_a_trimmed_name`, `Name_of_50_characters_is_accepted_and_51_is_rejected`, `Every_role_can_list_and_read_units`. Added: `A/TextLengthAcceptanceTests` `AC_06_1_BUG_001_unit_name_of_26_emoji_is_accepted`. Round 1: `I/TextLengthLimitTests` `Unit_name_of_50_characters_with_emoji_is_accepted_and_51_is_refused`; `U/Common/TextLengthTests` | Covered | pass | Failed in round 0 (BUG-001, minor: 26 emoji refused as more than 50 characters). Fixed by decision #116 and closed in round 1. |
| AC-06.2 | `I/UnitsTests` `Duplicate_name_ignoring_case_is_rejected`, `Empty_name_is_rejected`, `Simultaneous_creates_of_one_name_save_only_one_unit`; `I/DataModelTests` unique index | Covered | pass | Case-insensitive duplicates also work for "ñandú"/"ÑANDÚ" (exploratory). |
| AC-06.3 | `I/UnitsTests` `Manager_renames_a_unit`, `Rename_follows_the_same_rules_as_create`, `Renaming_a_unit_to_its_own_name_in_another_case_is_allowed` | Covered | pass | |
| AC-06.4 | `I/UnitsTests` `Manager_deletes_an_unused_unit`, `Deleting_a_unit_used_by_an_item_is_rejected`, `Unit_in_use_check_covers_every_foreign_key_to_units` | Covered | pass | |
| AC-07.1 | `I/ItemsTests` `Manager_creates_an_item`, `Item_with_description_and_type_in_any_letter_case`, `Duplicate_name_ignoring_case_is_rejected` | Covered | pass | |
| AC-07.2 | `I/ItemsTests` `Invalid_field_is_rejected`, `Missing_name_unknown_type_and_unknown_unit_are_reported_together`, `Duplicate_name_ignoring_case_is_rejected` | Covered | pass | |
| AC-07.3 | `I/ItemsTests` `Every_field_can_change_while_the_item_is_not_in_use`, `Item_in_use_keeps_its_type_and_unit_and_cannot_be_deleted`, `Item_in_use_check_covers_every_foreign_key_to_items`; +4 | Covered | pass | |
| AC-07.4 | `I/ItemsTests` `Manager_deletes_an_unused_item`, `Item_in_use_keeps_its_type_and_unit_and_cannot_be_deleted`; `I/RecipesTests` `Recipe_puts_its_product_and_materials_in_use` | Covered | pass | |
| AC-07.5 | `I/ItemsTests` `Every_role_can_list_and_view_items`, `Manager_creates_an_item` | Covered | pass | |
| AC-08.1 | `I/RecipesTests` `Prd_example_round_trips_with_units`, `Saving_again_replaces_all_previous_lines`, `Every_role_can_view_a_recipe`; +3 | Covered | pass | |
| AC-08.2 | `I/RecipesTests` `All_line_problems_are_reported_together`, `Recipe_for_a_raw_material_is_rejected`, `Trailing_zeros_do_not_count_as_decimal_places` | Covered | pass | |
| AC-08.3 | `I/RecipesTests` `Prd_example_round_trips_with_units`; `U/Calculations/PlannedAmountAndRangeTests` `AC_08_3_one_kg_per_bag_times_1000_bags_plans_1000_kg` | Covered | pass | |
| AC-08.4 | `I/ProductionOrdersTests` `Changing_the_recipe_does_not_change_existing_orders`; `U/Calculations/CompletionTests` `AC_08_4_required_uses_the_amount_copied_onto_the_line` | Covered | pass | |
| AC-09.1 | `I/ReceiptsTests` `Warehouse_user_records_a_receipt_and_warehouse_stock_goes_up`, `Optional_note_past_date_and_three_decimals_are_accepted`; `I/TextLengthLimitTests` `Receipt_note_of_500_emoji_is_accepted_and_501_is_refused` (round 1) | Covered | pass | |
| AC-09.2 | `I/ReceiptsTests` `Invalid_field_is_rejected_and_stock_is_unchanged` (every AC-09.2 case: zero, negative, 4 decimals, missing and unknown item, tomorrow, note of 501, plus above the maximum and malformed values), `Receipt_date_is_compared_with_todays_utc_date`, `Several_invalid_fields_are_reported_together` | Covered | pass | |
| AC-09.3 | `I/ReceiptsTests` `Receipt_for_a_finished_product_is_rejected` | Covered | pass | |
| AC-09.4 | `I/ReceiptsTests` `Warehouse_user_records_a_receipt_and_warehouse_stock_goes_up` | Covered | pass | |
| AC-10.1 | `I/StockTests` `Stock_is_the_sum_of_the_items_movements_in_each_location`, `Stock_is_listed_newest_item_first_and_filtered_by_type`. Added: `A/StockZeroFormsAcceptanceTests` `AC_10_1_location_whose_movements_cancel_out_shows_a_plain_zero_number` | Covered | pass | Routed item, see 3.1. |
| AC-10.2 | `I/StockTests` `Item_without_movements_has_zero_stock_and_every_role_can_read_it`. Added: `A/StockZeroFormsAcceptanceTests` `AC_10_2_item_without_movements_shows_a_plain_zero_number` | Covered | pass | |
| AC-10.3 | `I/StockTests` `Raw_materials_show_committed_pending_and_free_leftover`, `Finished_product_has_null_leftover_figures`; `I/ReturnsTests` `Manager_instructs_a_return_and_it_stops_counting_as_free_leftover` | Covered | pass | |
| AC-10.4 | `I/StockTests` `Stock_of_an_unknown_item_is_404` | Covered | pass | |
| AC-11.1 | `I/MovementsTests` `A_movement_shows_every_design_field`, `List_rows_equal_the_single_movement`, `Every_role_can_list_and_read_movements` | Covered | pass | |
| AC-11.2 | `I/MovementsTests` `Each_filter_returns_the_matching_movements_newest_first` (26 cases incl. day edges), `Filters_combine_with_and`, `No_match_is_200_with_an_empty_list` | Covered | pass | |
| AC-11.3 | `I/MovementsTests` `Movements_cannot_be_edited_deleted_or_added`; `I/DataModelTests` `Saved_movement_cannot_be_changed_or_deleted`; `S/RoleMatrixTests` endpoint table; +4 | Covered | pass | |
| AC-11.4 | `I/MovementsTests` `Listed_changes_add_up_to_the_stock_of_each_location`; `I/StockTests` `Stock_is_the_sum_of_the_items_movements_in_each_location` | Covered | pass | |
| AC-12.1 | `I/AdjustmentsTests` `Manager_adjusts_warehouse_stock_and_gets_the_movement`, `Positive_and_negative_adjustments_change_production_storage_only`, `Boundary_values_are_accepted`. Added: `A/TextLengthAcceptanceTests` `AC_12_1_BUG_001_reason_of_260_emoji_is_accepted`. Round 1: `I/TextLengthLimitTests` `Adjustment_reason_of_500_emoji_is_accepted_and_501_is_refused` (the stock ledger's re-check counts the same way) | Covered | pass | Failed in round 0 (BUG-001, minor: 251 to 500 emoji refused). Fixed by decision #116 and closed in round 1. |
| AC-12.2 | `I/AdjustmentsTests` `Bad_fields_are_rejected_with_their_message` (13 cases: zero, -0.000, missing, 4 decimals, missing/empty/blank/501 reason, item, location) | Covered | pass | |
| AC-12.3 | `I/AdjustmentsTests` `Negative_adjustment_larger_than_the_stock_is_rejected_and_exactly_zero_is_allowed` (the PRD's 12 kg example word for word); `S/CrossEndpointRaceTests` (4 forced orderings); +5 | Covered | pass | |
| AC-12.4 | `I/AdjustmentsTests` `Write_off_lowers_the_next_orders_leftover_estimate` | Covered | pass | |
| AC-12.5 | `I/AdjustmentsTests` `Production_storage_adjustment_of_a_material_in_use_is_rejected`, `Material_can_be_adjusted_after_the_order_is_completed_or_cancelled`, `Adjustment_racing_a_send_never_distorts_the_order`; +4 | Covered | pass | |
| AC-12.6 | `I/AdjustmentsTests` `Finished_product_in_production_storage_is_rejected`, `Finished_products_can_be_adjusted_in_the_warehouse` | Covered | pass | |
| AC-12.7 | `I/AdjustmentsTests` `Users_without_the_manager_role_get_403`; `S/RoleMatrixTests` | Covered | pass | |

### US-13 to US-18: orders, sends and complementary orders

| Criterion | Tests that prove it | Before QA | Result | Notes |
|-----------|--------------------|-----------|--------|-------|
| AC-13.1 | `I/ProductionOrdersTests` `Manager_creates_an_order_with_the_design_example_detail`, `Trailing_zeros_are_accepted_and_planned_amount_is_rounded`, `Simultaneous_creates_get_sequential_unique_numbers`; +4 unit | Covered | pass | |
| AC-13.2 | `I/ProductionOrdersTests` `Estimates_follow_the_oldest_order_first_example`, `Estimate_reads_one_snapshot_even_when_a_send_commits_in_between`; `I/SendOrderTests` `Send_applies_exactly_the_leftover_the_estimate_showed`; +4 | Covered | pass | |
| AC-13.3 | `I/ProductionOrdersTests` `Bad_ordered_quantity_is_rejected`, `Order_for_a_raw_material_is_rejected`, `Order_for_a_product_without_a_recipe_is_rejected` | Covered | pass | |
| AC-13.4 | `I/ProductionOrdersTests` `Creating_an_order_creates_no_movement` | Covered | pass | |
| AC-13.5 | `I/ProductionOrdersTests` `Orders_can_be_created_while_another_is_in_production` | Covered | pass | |
| AC-13.6 | `I/ProductionOrdersTests` `Orders_cannot_be_edited_or_deleted` (PUT, PATCH, DELETE 405, order unchanged); `S/RoleMatrixTests` `Table_lists_every_endpoint_the_app_serves_with_its_roles` (no other endpoint exists) | Covered | pass | |
| AC-13.7 | `I/ProductionOrdersTests` `Only_a_manager_can_create_an_order`; `S/RoleMatrixTests` | Covered | pass | |
| AC-14.1 | `I/ProductionOrdersTests` `List_is_newest_first_filtered_by_status_and_paged`, `Every_role_can_list_and_view_orders` | Covered | pass | |
| AC-14.2 | `I/ProductionOrdersTests` `Manager_creates_an_order_with_the_design_example_detail`, `In_production_order_shows_send_figures_and_no_estimate`, `Completed_order_shows_stored_percentages_rounded_to_two_places`, `Cancelled_order_shows_its_reason_and_no_estimate`; `I/CompleteOrderTests` `Example_A_within_tolerance_registers_output_and_two_notifications` | Covered | pass | |
| AC-14.3 | `I/ProductionOrdersTests` `Unknown_order_number_is_404`; +2 | Covered | pass | |
| AC-15.1 | `I/SendOrderTests` `Send_nets_the_leftover_moves_the_stock_and_starts_production` | Covered | pass | |
| AC-15.2 | `I/SendOrderTests` `Sends_inside_the_range_are_accepted`, `Sends_outside_the_range_without_leftover_are_rejected`; +3 unit | Covered | pass | |
| AC-15.3 | `I/SendOrderTests` `Send_nets_the_leftover_moves_the_stock_and_starts_production`, `Sends_outside_the_netted_range_are_rejected`; `S/WorkedExampleScenarioTests` `Example_A_within_tolerance_end_to_end`; +4 | Covered | pass | |
| AC-15.4 | `I/SendOrderTests` `Send_of_zero_is_accepted_when_leftover_covers_the_need`; `S/WorkedExampleScenarioTests` `Oldest_order_first_and_example_D_end_to_end`; +6 | Covered | pass | |
| AC-15.5 | `I/ProductionOrdersTests` `Estimates_follow_the_oldest_order_first_example`; `I/SendOrderTests` `Newer_order_sent_first_applies_only_unallocated_leftover`, `Each_unit_of_leftover_is_applied_to_one_order_only`; `I/CancelOrderTests` `Cancelling_order_7_releases_its_leftover_to_order_8`; `S/WorkedExampleScenarioTests` `Oldest_order_first_and_example_D_end_to_end`; +7 | Covered | pass | |
| AC-15.6 | `I/SendOrderTests` `Every_line_outside_its_range_is_named`, `Sends_outside_the_netted_range_are_rejected` (PRD message word for word), `Errors_follow_the_order_of_the_body_not_the_recipe` | Covered | pass | |
| AC-15.7 | `I/SendOrderTests` `Every_line_short_of_warehouse_stock_is_named`; `S/CrossEndpointRaceTests` `Send_waiting_behind_a_write_off_is_refused` | Covered | pass | |
| AC-15.8 | `I/SendOrderTests` `Bad_send_bodies_are_rejected`, `Every_body_problem_is_reported_at_once` | Covered | pass | |
| AC-15.9 | `I/SendOrderTests` `Second_send_of_an_order_is_rejected`, `Orders_that_are_not_created_cannot_be_sent`, `Simultaneous_sends_of_the_same_order_succeed_once` | Covered | pass | Five simultaneous live clicks: one 200, four 409 (exploratory). |
| AC-15.10 | `I/SendOrderTests` `Simultaneous_sends_of_two_orders_start_only_one`, `Unique_in_production_index_is_the_backstop`; `I/DataModelTests` `Second_InProduction_order_is_refused_by_the_database`; +3 | Covered | pass | |
| AC-15.11 | `I/SendOrderTests` `Pending_return_of_a_material_blocks_the_send`, `Every_pending_return_is_named_in_recipe_line_order`; `I/ReturnsTests` `Pending_return_blocks_the_send_until_it_is_received`, `Send_waiting_behind_an_instruction_is_refused` | Covered | pass | |
| AC-15.12 | `I/SendOrderTests` `Production_only_user_cannot_send`; `S/RoleMatrixTests` | Covered | pass | |
| AC-16.1 | `I/ComplementaryOrdersTests` `Production_user_requests_extra_material_for_the_running_order` (number, link, requester, time, notification text and data), `Manager_can_raise_a_request_and_approve_it_themselves`; `I/TextLengthLimitTests` `Complementary_request_reason_of_500_emoji_is_accepted_and_501_is_refused` (round 1) | Covered | pass | |
| AC-16.2 | `I/ComplementaryOrdersTests` `Request_for_an_order_not_in_production_is_refused`; +3 races | Covered | pass | |
| AC-16.3 | `I/ComplementaryOrdersTests` `Invalid_request_is_rejected`, `Every_problem_is_reported_in_one_response` | Covered | pass | |
| AC-16.4 | `I/ComplementaryOrdersTests` `A_parent_can_have_several_complementary_orders_each_decided_on_its_own`; `I/ComplementarySendTests` `Several_complementary_sends_add_up_on_the_parent_line` | Covered | pass | |
| AC-16.5 | `I/ComplementaryOrdersTests` `Production_user_requests_extra_material_for_the_running_order` (exact shape, no ordered or produced quantity); `I/ComplementarySendTests` `Warehouse_user_sends_an_approved_complementary_order` (only SentToProduction movements) | Covered | pass | |
| AC-16.6 | `I/ComplementaryOrdersTests` `Warehouse_only_user_cannot_request`; `S/RoleMatrixTests` | Covered | pass | |
| AC-17.1 | `I/ComplementaryOrdersTests` `Manager_approves_a_pending_request`, `Manager_can_raise_a_request_and_approve_it_themselves` | Covered | pass | |
| AC-17.2 | `I/ComplementaryOrdersTests` `Manager_rejects_a_pending_request_with_a_note`, `Reject_without_a_note`; `I/ComplementarySendTests` `Pending_rejected_and_already_sent_orders_cannot_be_sent`; `I/TextLengthLimitTests` `Rejection_note_of_500_emoji_is_accepted_and_501_is_refused` (round 1) | Covered | pass | |
| AC-17.3 | `I/ComplementaryOrdersTests` `Only_a_pending_request_can_be_approved_or_rejected`, `Simultaneous_approvals_succeed_once`, `Simultaneous_approve_and_reject_decide_once` | Covered | pass | |
| AC-17.4 | `I/ComplementaryOrdersTests` `Users_without_the_manager_role_cannot_approve_or_reject`; `S/RoleMatrixTests` | Covered | pass | |
| AC-18.1 | `I/ComplementarySendTests` `Warehouse_user_sends_an_approved_complementary_order`, `A_line_of_zero_moves_nothing`, `Invalid_send_is_rejected`; +6 | Covered | pass | |
| AC-18.2 | `I/ComplementarySendTests` `Sends_within_the_range_are_accepted`, `Sends_outside_the_range_are_rejected`; +2 unit | Covered | pass | |
| AC-18.3 | `I/ComplementarySendTests` `Only_an_approved_complementary_order_can_be_sent`, `Pending_rejected_and_already_sent_orders_cannot_be_sent`; +8 races | Covered | pass | |
| AC-18.4 | `I/ComplementarySendTests` `Short_warehouse_stock_names_every_short_line`; `S/CrossEndpointRaceTests` `Complementary_send_waiting_behind_a_write_off_is_refused` | Covered | pass | |
| AC-18.5 | `I/ComplementarySendTests` `Production_only_user_cannot_send`; `S/RoleMatrixTests` | Covered | pass | |

### US-19 to US-23: completion, variance, cancellation, returns, notifications

| Criterion | Tests that prove it | Before QA | Result | Notes |
|-----------|--------------------|-----------|--------|-------|
| AC-19.1 | `I/CompleteOrderTests` `Example_A_within_tolerance_registers_output_and_two_notifications`, `Leftover_above_zero_needs_a_reason`; `I/TextLengthLimitTests` `Completion_reason_of_500_emoji_is_accepted_and_501_is_refused` (round 1) | Covered | pass | |
| AC-19.2 | `I/CompleteOrderTests` `Example_A_...` (exactly one ConsumedInProduction −1,016 and one ProducedOutput +1,001), `Required_amount_of_zero_without_usage_gives_a_null_percent_and_no_movement`, `Stock_changed_during_the_run_follows_AC_19_2_and_logs_a_warning`; +3 | Covered | pass | |
| AC-19.3 | `I/CompleteOrderTests` `Example_A_...`, `Example_C_under_use_notifies_and_zero_output_variance_does_not`, `Example_D_subtracts_carried_leftover_and_leaves_it_for_the_older_order`; +4 | Covered | pass | |
| AC-19.4 | `I/CompleteOrderTests` `Bad_completion_bodies_are_rejected`, `Leftover_above_zero_needs_a_reason`, `Leftover_cannot_exceed_production_storage` (PRD message), `Every_leftover_above_its_stock_is_named` | Covered | pass | |
| AC-19.5 | `I/CompleteOrderTests` `Second_completion_is_rejected_and_changes_nothing`, `Orders_that_are_not_in_production_cannot_be_completed`, `Simultaneous_completions_register_output_once`; +3 | Covered | pass | Five simultaneous live clicks: one 200, four 409. |
| AC-19.6 | `I/CompleteOrderTests` `Unsent_complementary_orders_are_cancelled_on_completion`; `I/ComplementaryOrdersTests` `Completing_the_parent_cancels_its_pending_and_approved_requests`; +5 races | Covered | pass | |
| AC-19.7 | `I/CompleteOrderTests` `Example_A_...`, `Reason_with_no_leftover_is_stored_trimmed_without_a_leftover_notification`, `Leftover_notification_longer_than_1000_characters_is_stored_whole`; +7 | Covered | pass | |
| AC-19.8 | `I/CompleteOrderTests` `Warehouse_only_user_cannot_complete`; `S/RoleMatrixTests` | Covered | pass | |
| AC-20.1 | `I/CompleteOrderTests` `Example_A_...`, `Example_C_...` (zero output variance recorded) | Covered | pass | |
| AC-20.2 | `I/CompleteOrderTests` `Example_C_...`; `U/NotificationComposerTests` `OutputVariance_reproduces_the_design_example`; +4. Added: `A/DisplayedPercentAcceptanceTests` `Decision_58_output_variance_percent_rounds_in_two_steps_end_to_end` | Covered | pass | |
| AC-20.3 | `I/CompleteOrderTests` `Example_A_...`; `S/WorkedExampleScenarioTests` `Example_A_within_tolerance_end_to_end` | Covered | pass | |
| AC-20.4 | `I/CompleteOrderTests` `Exactly_the_tolerance_does_not_notify_and_just_above_does`, `Example_B_above_tolerance_raises_a_usage_variance_notification`; `U/Calculations/CompletionTests` (±2.00%, ±2.0001%); +8. Added: `A/DisplayedPercentAcceptanceTests` `Decision_58_usage_variance_percent_rounds_in_two_steps_and_notifies_on_the_unrounded_value` | Covered | pass | |
| AC-20.5 | `I/CompleteOrderTests` `Example_A_...`; `S/WorkedExampleScenarioTests` `Example_A_within_tolerance_end_to_end` (HTTP only, from an empty database); +7 | Covered | pass | |
| AC-20.6 | `I/CompleteOrderTests` `Example_B_above_tolerance_raises_a_usage_variance_notification`; `S/WorkedExampleScenarioTests` `Example_B_above_tolerance_end_to_end`; +3 | Covered | pass | |
| AC-20.7 | `I/CompleteOrderTests` `Example_C_under_use_notifies_and_zero_output_variance_does_not`; `S/WorkedExampleScenarioTests` `Example_C_under_use_then_leftover_returned_end_to_end`; +4 | Covered | pass | |
| AC-20.8 | `I/CompleteOrderTests` `Example_D_subtracts_carried_leftover_and_leaves_it_for_the_older_order`; `S/WorkedExampleScenarioTests` `Oldest_order_first_and_example_D_end_to_end`; +6 | Covered | pass | |
| AC-20.9 | `I/CompleteOrderTests` `Example_A_...` (exactly two completion movements) | Covered | pass | |
| AC-20.10 | `I/CompleteOrderTests` `Changing_the_tolerance_later_changes_no_recorded_value`; `I/ProductionOrdersTests` `Completed_order_shows_stored_percentages_rounded_to_two_places` | Covered | pass | |
| AC-21.1 | `I/CancelOrderTests` `Created_order_is_cancelled_with_who_when_and_why_and_moves_no_stock`, `In_production_order_is_cancelled_and_managers_are_told_what_material_is_left`, `Reason_of_exactly_500_characters_is_accepted_after_trimming`; `I/TextLengthLimitTests` `Cancellation_reason_of_500_emoji_is_accepted_and_501_is_refused` (round 1) | Covered | pass | |
| AC-21.2 | `I/CancelOrderTests` `Created_order_is_cancelled_...`, `Cancelling_order_7_releases_its_leftover_to_order_8`; +4 | Covered | pass | |
| AC-21.3 | `I/CancelOrderTests` `In_production_order_is_cancelled_and_managers_are_told_what_material_is_left`, `In_production_order_with_nothing_in_production_storage_raises_no_notification`, `After_cancelling_the_order_in_production_another_order_can_be_sent_with_its_material`; +6 | Covered | pass | |
| AC-21.4 | `I/CancelOrderTests` `Unsent_complementary_orders_are_cancelled_with_the_order`; +5 | Covered | pass | |
| AC-21.5 | `I/CancelOrderTests` `Completed_and_cancelled_orders_cannot_be_cancelled`, `Missing_blank_or_too_long_reason_is_rejected`, `Simultaneous_cancellations_succeed_once`; +2 | Covered | pass | |
| AC-21.6 | `I/CancelOrderTests` `Users_without_the_manager_role_cannot_cancel`; `S/RoleMatrixTests` | Covered | pass | |
| AC-22.1 | `I/ReturnsTests` `Manager_instructs_a_return_and_it_stops_counting_as_free_leftover`, `Order_link_is_optional_and_may_name_an_order_in_any_status`, `Invalid_instruction_is_rejected`; `I/TextLengthLimitTests` `Return_reason_of_500_emoji_is_accepted_and_501_is_refused` (round 1) | Covered | pass | |
| AC-22.2 | `I/ReturnsTests` `Only_free_leftover_can_be_returned`, `Invalid_instruction_is_rejected`, `Instruction_waiting_behind_an_instruction_counts_its_pending_return` | Covered | pass | |
| AC-22.3 | `I/ReturnsTests` `Material_of_the_order_in_production_cannot_be_returned`; +2 races | Covered | pass | |
| AC-22.4 | `I/ReturnsTests` `Warehouse_user_receives_a_return_and_the_stock_moves`, `Returned_leftover_is_no_longer_in_the_next_orders_estimate`; +4 | Covered | pass | |
| AC-22.5 | `I/ReturnsTests` `Manager_cancels_a_pending_return_and_the_leftover_is_free_again`, `Received_return_cannot_be_received_again_or_cancelled`; +5 races | Covered | pass | |
| AC-22.6 | `I/ReturnsTests` `Production_only_user_can_view_returns_but_not_act_on_them`, `Warehouse_only_user_can_receive_but_not_instruct_or_cancel`; `S/RoleMatrixTests` | Covered | pass | |
| AC-23.1 | `I/NotificationsTests` `Each_notification_shows_every_design_field_and_its_data_as_stored`, `UnreadOnly_filters_to_what_the_caller_has_not_read`, `List_is_newest_first_with_the_id_breaking_ties`; +6 | Covered | pass | |
| AC-23.2 | Each of the five events and its structured values: `I/ComplementaryOrdersTests` `Production_user_requests_...`, `I/CompleteOrderTests` `Example_A_...`/`Example_B_...`, `I/CancelOrderTests` `In_production_order_is_cancelled_...`; `U/NotificationComposerTests` (5 design examples word for word). Added: `A/NotificationEventsAcceptanceTests` `AC_23_2_notifications_are_created_only_by_the_five_events` | **Partly**: "created only by these events" was checked next to a few actions, not across the other data-changing endpoints | pass | |
| AC-23.3 | `I/NotificationsTests` `Marking_read_is_for_the_calling_Manager_only`, `Read_all_marks_everything_for_the_caller_only`, `Simultaneous_marks_by_one_Manager_all_succeed`; +3 | Covered | pass | |
| AC-23.4 | `I/NotificationsTests` `A_Manager_created_later_sees_every_earlier_notification_as_unread` | Covered | pass | |
| AC-23.5 | `I/NotificationsTests` `An_unknown_id_is_404`, `Notifications_cannot_be_deleted_or_edited` | Covered | pass | |
| AC-23.6 | `I/NotificationsTests` `Users_without_the_Manager_role_get_403_on_every_endpoint`, `Every_endpoint_needs_a_token`; `S/RoleMatrixTests` | Covered | pass | |

### Non-functional requirements (PRD section 5, and the precision rules of 4.1)

| Requirement | Tests that prove it | Before QA | Result | Notes |
|-------------|--------------------|-----------|--------|-------|
| Platforms: builds and runs on Windows 11 with the documented commands; OpenAPI lists every endpoint | `I/OpenApiTests` (48 endpoints, Bearer, shapes, `.http` covers every endpoint); QA ran build, tests, `dotnet run` and the `.http` walk-through on Windows 11 (section 1) | Covered | pass | |
| Performance: 100,000 movements, 1,000 orders, 500 items, 95% within 1 s | `I/StockTests+Performance` `Stock_reads_meet_the_performance_target_with_100000_movements`; `I/MovementsTests+Performance`. Added: `A/PerformanceAcceptanceTests` `PRD_5_order_item_notification_reads_and_writes_meet_the_target_at_full_volume` | **Partly**: only stock and movement reads were timed; orders, order detail with estimate, items, notifications and every write were not (architecture risk 3) | pass | Measured p95 at full volume: order list 3 to 6 ms, order detail 5 ms, items 3 ms, notifications 5 ms, receipt 3 ms, adjustment 7 ms, create 10 ms, send 17 ms, complete 22 ms. In-process test server; not measured under many parallel writers. |
| Consistency under concurrent use | `I/SendOrderTests` (same order twice, two orders), `I/StockTests` `Simultaneous_withdrawals_cannot_both_use_the_same_stock`, `Ten_simultaneous_withdrawals_never_take_stock_below_zero`, `Mixed_simultaneous_movements_never_take_either_location_below_zero`; `S/CrossEndpointRaceTests`; forced orderings in every write feature | Covered | pass | Live: 5 simultaneous sends, completes and creates behaved correctly. |
| Security: token on every endpoint but sign-in; salted hashes; password policy; secrets from environment variables, never committed | AC-01.5 rows above; `I/AuthTests` `Passwords_are_stored_only_as_salted_hashes`; `I/UsersTests` and `I/StartupTests` policy tests; `I/StartupTests` `Environment_variable_cannot_fill_a_setting_left_out`, `Start_up_does_not_log_secrets`. Added: `A/SecretsConfigurationAcceptanceTests`. Round 1: `I/PasswordLengthValidatorTests` `Six_characters_with_emoji_are_too_short`, `Eight_characters_with_emoji_are_long_enough`, `Ascii_boundary_is_unchanged`, `Identity_alone_accepts_six_emoji_characters_and_the_registered_set_refuses_them` | **Partly**: "never from committed files" was not checked (same gap as AC-02.5) | pass | Round 1: since decision #118, the 8-character minimum counts characters (code points); before it, "Aa1🧀🧀🧀" (6 characters) was accepted. `code-reviewer` found that deviation while reviewing BUG-001; QA's round 0 had missed it. Deeper review is phase 6. |
| Configuration: tolerance in settings, default 2; outside 0 to 100 stops start-up | `I/StartupTests` `Tolerance_outside_0_to_100_stops_start_up`, `Tolerance_at_the_limits_is_accepted`; `I/CompleteOrderTests` `Changing_the_tolerance_later_changes_no_recorded_value` | Covered | pass | |
| Data: kept indefinitely, never deleted | `I/MovementsTests`, `I/ProductionOrdersTests`, `I/UsersTests`, `I/NotificationsTests` (DELETE is 405); `S/RoleMatrixTests` (no delete endpoint except unused units and items) | Covered | pass | |
| Language: English messages and fields, UTC times | Every error test compares the exact English message; the feature tests compare times as UTC `...Z` strings (for example `2026-11-11T11:20:00Z`) | Covered | pass | |
| Precision (4.1): 3 and 6 decimal places, rounding half away from zero, percentages round(round(x, 6), 2), threshold on the unrounded value | `U/Common/QuantityTests` (incl. `DisplayPercent_rounds_the_six_place_value_to_two_places`: 2.0049996 shows 2.01), `U/Calculations/*`; decimal-place cases in every input test; `I/ProductionOrdersTests` `Completed_order_shows_stored_percentages_rounded_to_two_places` (stored value written directly). Added: `A/DisplayedPercentAcceptanceTests` (2 tests) | **Partly**: the two-step display rule was not proven through a real completion (routed item, decision #58) | pass | See 3.1. |
| Accessibility | not applicable (PRD 5: no user interface) | n/a | n/a | |

## 3. Edge cases and exploratory testing

### 3.1 Routed QA items (from `docs/00-status.md`)

| Item | What was tried | Result |
|------|----------------|--------|
| Displayed percentages are round(round(x, 6), 2) (#58), which can differ from one rounding | Live and in `A/DisplayedPercentAcceptanceTests`: ordered 11,579, produced 11,590 → +0.0949995682...%; ordered and produced 19,601, usage 19,994 → +2.0049997449...% | pass. Order detail, notification message and `data.percent` all show **0.10** and **2.01** (one rounding would give 0.09 and 2.00). The 2.0049997% line is above the 2% tolerance and notifies. |
| A location with movements but no change shows `0.0`; an item with no movements shows `0` | Live: new item → `"warehouse":0`; +10.5/−10.5 in production storage and 1.25 in/out of the Warehouse → `"warehouse":0.0,"productionStorage":0.0`. `A/StockZeroFormsAcceptanceTests` | pass. Both are JSON numbers equal to zero, never `-0` or a string, single and list rows agree, movements sum to zero. Valid per design 2.4. |
| Without a token, any unknown path answers 401 | Live and in `A/UnknownPathAcceptanceTests`: `/`, `/nope`, `/api`, `/api/v1`, `/api/v2/items`, `/health/details`, `/api/v1/production-orders/abc`, `/api/v1/production-orders/99999999999`, POST/PUT/PATCH/DELETE on unknown or wrong-method paths | pass. All 401 `not-authenticated` with `WWW-Authenticate: Bearer`. With a token: 404 "No endpoint matches this path." or 405. |

### 3.2 Exploratory pass (time-boxed, real app in Development)

| Area | What was tried | Result |
|------|----------------|--------|
| Sample requests | Every request of `InventoryTracker.Api.http`, in order, on a fresh database | pass, 58 of 58 succeed |
| Unicode names | Unit "ñandú 🧀" then "ÑANDÚ 🧀"; item "Queso añejo 🧀" then "QUESO AÑEJO 🧀"; user `José.Núñez@ejemplo.com` | pass: stored as typed, duplicates found ignoring case. Emoji come back as `🧀` escapes, which is valid JSON |
| Text length in characters | Unit names of 25 and 26 emoji; reasons of 250, 251 and 260 emoji; 300 precomposed "é"; item name of 51 emoji | Round 0: **BUG-001 (minor)**: 26 emoji (26 characters) refused as "more than 50"; 251 emoji refused as "more than 500"; accented letters counted correctly. Round 1: fixed, see the last row |
| Odd text | Item names `<script>alert(1)</script>`, `Robert'); DROP TABLE Items;--`, a name with a tab, a NUL character, non-breaking spaces around it | Stored and returned as typed (parameterised SQL, nothing broke); NBSP trimmed. Tab and NUL accepted, which the PRD does not forbid: noted for the security review |
| Very large and very small numbers | Receipt 0.001, 999,999,999.999, 1,000,000,000, `1e30`, `1e-3`, `1E2`, `-0.0`, `"12.5"`, 28 decimal places, a 29-digit integer, `NaN`; order of 999,999,999.999 bags at 999,999.999999 per bag; amount per unit 1,000,000 and 0.0000001; adjustment −999,999,999.999 and −1,000,000,000 | pass: limits and messages per decision #80 and design 7.2; `1e30` gives "quantity has the wrong type." (known follow-up in `00-status.md`, cosmetic) |
| Rounding to zero | Order of 0.001 bags at 0.000001 kg per bag; 400 bags with 0.004 kg carried leftover and 0.003 left | pass: planned 0, range 0 to 0; completion gives percentage null, above tolerance, "(percentage not applicable)" (decisions #48, #49) |
| Dates | Receipt dates tomorrow, 2026-02-30, a date-time, 0001-01-01; movement filters `from` after `to`, `0001-01-01`, `9999-12-31` | pass; 0001-01-01 is accepted as a receipt date (PRD only forbids future dates) |
| Wrong order of steps | Complete a Created order; request a complementary order for a Created order; send and cancel a Cancelled order; approve or receive unknown numbers; receive a return twice | pass: the design 7.2 409 or 404 each time |
| Roles | Warehouse creating an order, Production reading notifications, Warehouse listing users, Production receiving a return, Warehouse cancelling one | pass: 403 with the design 7.1 message naming the needed role |
| Sign-in | Email in upper case and with spaces; password with a leading space | pass: email matches ignoring case and spaces; password is not trimmed (401) |
| Double clicks | 5 simultaneous sends of one order, then 5 simultaneous completions; 5 simultaneous order creations | pass: one 200 and four 409 each; 5 orders with 5 numbers |
| Paging | `page=0`, `pageSize=0`, `pageSize=101`, `page=999999`, `page=2147483648`, `pageSize=abc`, `status=created`, `status=Done` | pass, per design 2.5 and decision #113 |
| Unknown numbers | `GET /production-orders/0`, `/-1`, `/99999999999` | 0 and −1: "Order #0 not found."; a number beyond the integer range gets the generic "No endpoint matches this path." (still `404`, AC-14.3 holds; cosmetic, not filed) |
| Round 1 re-test: text limits and passwords (decisions #116, #118) | Fresh scratchpad database, throwaway environment variables. Unit names of 26 and 50 emoji, then 51 characters; adjustment reasons of 250, 251, 500 and 501 emoji; 25 and 26 x (e + combining accent) as a unit name; `POST /users` with passwords "Aa1🧀🧀🧀", "Aa1🧀🧀🧀🧀", "Aa1🧀🧀🧀🧀🧀", "Short1a", "Abcdefg1", "aaa🧀🧀🧀🧀🧀", "AAA1🧀🧀🧀🧀"; own change and Manager reset to 6- and 8-character emoji passwords | pass: 26 and 50 emoji `201`, 51 `400`; 250, 251 and 500 emoji `201` and stored as sent, 501 `400`; combining letters count as two code points (25 accepted, 26 refused), as #116 defines; 6- and 7-character passwords `400` `weak-password`, 8 characters `201` and sign-in `200`; ASCII boundary unchanged; the digit and lower-case rules still refuse; change and reset refuse 6 characters and accept 8 (`204`, sign-in `200`) |

## 4. Design conformance

The product has no screens. The API was checked against `docs/02-design.md`: status codes and order of checks (2.2), field names, nulls and enums in any letter case (2.3), number formats in messages (6.1), ProblemDetails body and titles (6), every message seen in 7.1 to 7.3, start-up behaviour (7.4). Round 0 found one deviation, BUG-001 (design 2.6 limits are in characters). It was fixed and closed in round 1. Seen and accepted: numbers carry varying trailing zeros (`1.0`, `100.0`, `0.00`), which design 2.4 says clients must not rely on.

## 5. Not tested

- **Writes under many parallel clients** at full data volume: the performance test times writes one at a time. SQLite serialises writers (architecture risk 3), so a load test with many simultaneous writers would be needed to state a 95th percentile for that case. Needs a load tool or a dedicated test; not in the focused scope.
- **Performance over a real network socket**: timings were taken on the machine this session ran on (Windows 11 Home), through the in-process test server, as the T-020 performance tests do. Whether that machine is the owner's is for the delivery manager to confirm.
- **Security in depth** (rate limiting, the lock-out denial of service, token handling, NUL and control characters in names): phase 6, `security-reviewer`.

## 6. Bugs

| ID | Severity | Summary | Status |
|----|----------|---------|--------|
| [BUG-001](bugs/BUG-001-text-limits-count-code-units.md) | minor | Text length limits count UTF-16 code units, so an emoji counts as two characters | closed (fixed by decision #116, re-tested in round 1) |

Related, without a QA bug report: the password minimum also counted UTF-16 code units. `code-reviewer` found it while reviewing the fix, decision #118 fixed it, and QA re-tested it in round 1 (section 3.2, last row).

Documents vs. build: no disagreement found between the PRD, the design and the build, apart from AC-01.5's wording, which says every endpoint except sign-in needs a token, while `/health` and the Development-only `/openapi/v1.json` are anonymous. The owner accepted that exception (decisions #28, #33); the PRD text was not changed. Reported for the record, not decided here.

## 7. Running the automated tests

```
dotnet test --artifacts-path TestResults/qa-art
```

Results this session, all on 2026-11-21 with `--artifacts-path TestResults/qa-art`:

| Run | Command | Result |
|-----|---------|--------|
| Baseline, before QA's tests | `dotnet build`, then `dotnet test` | build 0 warnings, 0 errors; 1,626 total, 1,626 passed, 0 failed, 0 skipped (2 min 00 s) |
| Acceptance tests only | `dotnet test --no-build --filter-namespace "InventoryTracker.Api.Tests.Acceptance"` | 31 total, 31 passed (before the BUG-001 tests were added) |
| BUG-001 tests with `Skip` removed | `dotnet test --no-build --filter-class "...Acceptance.TextLengthAcceptanceTests"` | 2 total, 2 failed: `Expected: Created`, `Actual: BadRequest` (the defect); `Skip` then restored |
| Final build | `dotnet build` | 0 warnings, 0 errors |
| Full suite, run 1 | `dotnet test --no-build` | 1,659 total, 1,657 passed, 0 failed, 2 skipped (BUG-001) (1 min 55 s) |
| Full suite, run 2 | `dotnet test --no-build` | 1,659 total, 1,657 passed, 0 failed, 2 skipped (BUG-001) (1 min 55 s) |
| Round 1: build after the fixes for #116 and #118 | `dotnet build` | 0 warnings, 0 errors |
| Round 1: BUG-001 acceptance tests, now un-skipped | `dotnet test --no-build --filter-class "...Acceptance.TextLengthAcceptanceTests"` | 2 total, 2 passed, 0 skipped |
| Round 1: full suite | `dotnet test --artifacts-path TestResults/qa-art` | **1,695 total, 1,695 passed, 0 failed, 0 skipped** (2 min 02 s) |

No test is skipped. The two BUG-001 tests in `Acceptance/TextLengthAcceptanceTests.cs` now run every time and guard the fix. Run only the acceptance tests with `dotnet test --artifacts-path TestResults/qa-art --filter-namespace "InventoryTracker.Api.Tests.Acceptance"`.

## 8. Summary

- **Before QA's work**, of 130 acceptance criteria: **127 covered, 2 partly covered** (AC-03.6, AC-23.2), **1 not covered** (AC-02.5). Of the 8 applicable non-functional requirements: 5 covered, 3 partly covered (performance, security, precision).
- **Tests added by QA**: 14 test methods (33 test cases) in 8 classes in `tests/InventoryTracker.Api.Tests/Acceptance/`. In round 0, 31 passed and the 2 BUG-001 tests were skipped because they failed; in round 1 all 33 pass. The fixes for #116 and #118 added developer tests, now listed in section 2: `Integration/TextLengthLimitTests`, `Unit/Common/TextLengthTests`, `Integration/PasswordLengthValidatorTests`, and emoji cases in `UsersTests` and `StartupTests`. The suite is now 1,695 tests, 0 skipped. After them, every criterion and non-functional requirement is covered, except the parallel-writer load case in section 5.
- **Bugs found**: 0 blocker, 0 major, 1 minor (BUG-001), now closed. Open bugs: none.
- **Verdict (round 1): PASS. Ready for security review.**
