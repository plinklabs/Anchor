import 'package:flutter/material.dart';
import 'package:plink_design_system/plink_design_system.dart';

import '../api/sessions_api.dart' show ApiException;

/// A user-facing presentation of a failed API call (#278).
///
/// The dashboard must never dump a raw exception `toString()` into the UI — a
/// bodyless 403 renders as a bare, meaningless `ApiException(403):`. This type
/// carries the human message plus whether the failure is an *authorization*
/// one (a 403: the caller is signed in but lacks the Teacher role, because
/// their Entra app role isn't assigned yet). Authorization failures aren't
/// something a teacher can fix by retrying, so pages render them as a calm
/// notice rather than a red error.
class ApiErrorMessage {
  const ApiErrorMessage(this.text, {this.isAuthorization = false});

  final String text;

  /// True for a 403 — the signed-in account is authenticated but not yet
  /// provisioned with the Teacher role. Drives the calm (non-error) styling.
  final bool isAuthorization;
}

/// Maps a thrown API error to an [ApiErrorMessage] — never the raw exception
/// `toString()`. A 403 becomes a specific, actionable not-authorized notice
/// flagged [ApiErrorMessage.isAuthorization]; every other failure (transient
/// network, 5xx, a 4xx with no special handling) falls back to [generic], a
/// context-specific human sentence the caller supplies.
///
/// Both messages are passed in already localized (#321): this helper runs
/// outside a build (in catch blocks) so it can't resolve `AppLocalizations`
/// itself — callers supply [generic] and [notAuthorized] from
/// `AppLocalizations.of(context)`.
ApiErrorMessage describeApiError(
  Object error, {
  required String generic,
  required String notAuthorized,
}) {
  if (error is ApiException && error.statusCode == 403) {
    return ApiErrorMessage(notAuthorized, isAuthorization: true);
  }
  return ApiErrorMessage(generic);
}

/// Renders an [ApiErrorMessage] with consistent styling so Home, History and
/// Classes can't drift (#278): an authorization (403) notice reads as calm ink
/// — it isn't an error the teacher can clear by retrying — while every other
/// failure reads in the theme's error colour.
class ApiErrorText extends StatelessWidget {
  const ApiErrorText(this.message, {super.key, this.textAlign});

  final ApiErrorMessage message;
  final TextAlign? textAlign;

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    return Text(
      message.text,
      textAlign: textAlign,
      style: theme.textTheme.bodyMedium?.copyWith(
        color: message.isAuthorization
            ? PlinkColors.ink60
            : theme.colorScheme.error,
      ),
    );
  }
}
