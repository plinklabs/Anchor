// ignore: unused_import
import 'package:intl/intl.dart' as intl;
import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for English (`en`).
class AppLocalizationsEn extends AppLocalizations {
  AppLocalizationsEn([String locale = 'en']) : super(locale);

  @override
  String get actionCancel => 'Cancel';

  @override
  String get actionDelete => 'Delete';

  @override
  String get actionRemove => 'Remove';

  @override
  String get actionAdd => 'Add';

  @override
  String get actionSave => 'Save';

  @override
  String get actionCreate => 'Create';

  @override
  String get actionImport => 'Import';

  @override
  String get actionClose => 'Close';

  @override
  String get actionRetry => 'Retry';

  @override
  String get actionLoadMore => 'Load more';

  @override
  String get actionApprove => 'Approve';

  @override
  String get badgeLive => 'Live';

  @override
  String get badgeEnded => 'Ended';

  @override
  String get badgeArchived => 'Archived';

  @override
  String get statusActive => 'Active';

  @override
  String get statusInactive => 'Inactive';

  @override
  String get commonLoading => 'Loading…';

  @override
  String get commonNone => '(none)';

  @override
  String get loginEyebrow => 'Anchor for teachers';

  @override
  String get loginHeadline => 'Ready when your class is.';

  @override
  String get loginSubtitle =>
      'Sign in with your school account to start a focus session for a class.';

  @override
  String get loginSignInButton => 'Sign in with Microsoft';

  @override
  String get loginTimeoutError =>
      'Signing in is taking longer than expected. Please try again.';

  @override
  String get loginError => 'Could not sign you in. Please try again.';

  @override
  String get shellNavHome => 'Home';

  @override
  String get shellNavClasses => 'Classes';

  @override
  String get shellNavHistory => 'History';

  @override
  String get shellNavAdmin => 'Admin';

  @override
  String get shellSignOut => 'SIGN OUT';

  @override
  String get shellSectionPastSessions => 'Past sessions';

  @override
  String get shellSectionLiveSession => 'Live session';

  @override
  String get shellSectionPastSession => 'Past session';

  @override
  String get adminNavBundles => 'Bundles';

  @override
  String get adminNavAdmins => 'Admins';

  @override
  String get adminNavSchools => 'Schools';

  @override
  String get apiError403 =>
      'Your account isn\'t set up as a teacher yet. Ask an administrator to grant access.';

  @override
  String get apiError403Admin =>
      'Your account doesn\'t have admin access. Ask an administrator to grant it.';

  @override
  String get homeLoadError =>
      'Could not load start-session data. Please try again.';

  @override
  String get homeStartError => 'Failed to start session. Please try again.';

  @override
  String get homeEndError => 'Failed to end session. Please try again.';

  @override
  String get homeActiveSessionFallback => 'Active session';

  @override
  String homeStillRunning(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Still running — $count sessions',
      one: 'Still running',
    );
    return '$_temp0';
  }

  @override
  String get homeHeadline => 'Start a focus session.';

  @override
  String get homeSubtitle =>
      'Pick a class and start. Students join with the code shown on the session screen.';

  @override
  String get homeNoClasses => 'No classes assigned to you yet.';

  @override
  String homeYourDepartment(String department) {
    return 'YOUR DEPARTMENT · $department';
  }

  @override
  String get homeClassLabel => 'Class';

  @override
  String get homeSelectClass => 'Select a class';

  @override
  String homeStartSessionFor(String name) {
    return 'Start session for $name';
  }

  @override
  String homeStartedAt(String time) {
    return 'STARTED $time';
  }

  @override
  String get homeResume => 'Resume';

  @override
  String get homeEnd => 'End';

  @override
  String get classesLoadError => 'Could not load classes. Please try again.';

  @override
  String get classesLoadSchoolsError =>
      'Couldn\'t load schools. The selector may be incomplete.';

  @override
  String get classesLoadRosterError =>
      'Could not load roster. Please try again.';

  @override
  String get classesDeleteClass => 'Delete class';

  @override
  String classesDeleteClassBody(String name, String year) {
    return 'Delete $name ($year)? This removes the class and its roster. Classes with past sessions cannot be deleted.';
  }

  @override
  String get classesDeleteError => 'Could not delete class. Please try again.';

  @override
  String get classesSaveCodesError =>
      'Could not save school + code. Please try again.';

  @override
  String get classesAddMemberError => 'Failed to add member. Please try again.';

  @override
  String get classesRemoveMemberTitle => 'Remove member';

  @override
  String classesRemoveMemberBody(String name, String className) {
    return 'Remove $name from $className? They will stop receiving session broadcasts on the next session start.';
  }

  @override
  String get classesRemoveMemberError =>
      'Failed to remove member. Please try again.';

  @override
  String get classesCsvNoRows => 'No valid rows in CSV.';

  @override
  String get classesImportError => 'Import failed. Please try again.';

  @override
  String get classesGraphImportError =>
      'Populate from Graph failed. Please try again.';

  @override
  String get classesPickClass => 'Pick a class on the left.';

  @override
  String get classesListHeader => 'Classes';

  @override
  String get classesNewClass => 'New class';

  @override
  String get classesEmpty =>
      'No classes you teach. Create one with \"New class\".';

  @override
  String get classesSetScopeFirst => 'Set school + code first';

  @override
  String get classesImportCsv => 'Import CSV';

  @override
  String get classesPopulateFromGraph => 'Populate from Graph';

  @override
  String get classesSchoolLabel => 'School';

  @override
  String get classesClassCodeLabel => 'Class code';

  @override
  String get classesHint3A => 'e.g. 3A';

  @override
  String get classesNoMembers => 'No members yet.';

  @override
  String get classesDisplayName => 'Display name';

  @override
  String get classesRole => 'Role';

  @override
  String classesImportAdded(int count) {
    return '$count added';
  }

  @override
  String classesImportAlready(int count) {
    return '$count already member';
  }

  @override
  String classesImportUnresolved(int count) {
    return '$count unresolved';
  }

  @override
  String classesImportWrongSchool(int count) {
    return '$count wrong school';
  }

  @override
  String get classesBlank => '(blank)';

  @override
  String get classesCreateError => 'Could not create class. Please try again.';

  @override
  String get classesNameLabel => 'Name';

  @override
  String get classesSchoolYearLabel => 'School year';

  @override
  String get classesSchoolYearHint => 'e.g. 2025-2026';

  @override
  String get classesSchoolOptionalLabel => 'School (optional)';

  @override
  String get classesClassCodeOptionalLabel => 'Class code (optional)';

  @override
  String get classesCsvDialogTitle => 'Import roster from CSV';

  @override
  String get classesCsvDialogBody =>
      'Paste a CSV with a header row. Required column: upn (the user principal name, e.g. student@school.be). Names are looked up in the directory automatically.';

  @override
  String get classesCsvHint => 'upn\nalice@school.be\nbob@school.be\n...';

  @override
  String get classesCsvEmpty => 'CSV is empty.';

  @override
  String get classesCsvHeaderMissing => 'Header must include upn.';

  @override
  String get sessionUpdateBundlesError =>
      'Could not update the allowed bundles. Please try again.';

  @override
  String get sessionApproveError =>
      'Could not approve the request. Please try again.';

  @override
  String sessionCopiedCode(String code) {
    return 'Copied $code';
  }

  @override
  String get sessionConnectError =>
      'Could not connect to the live feed for this session. Reload the page to try again.';

  @override
  String get sessionEndError => 'Failed to end session. Please try again.';

  @override
  String get sessionLeaveTitle => 'Leave this session?';

  @override
  String get sessionLeaveBody =>
      'This session is still running and students stay enforced. End it for everyone, or leave it running and come back to it later from the home screen?';

  @override
  String get sessionLeaveRunning => 'Leave running';

  @override
  String get sessionEndSession => 'End session';

  @override
  String get sessionTitleFallback => 'Session';

  @override
  String sessionTitleSpec(String datetime) {
    return 'Session $datetime';
  }

  @override
  String get sessionHeadlineFallback => 'Live session';

  @override
  String get sessionConnecting => 'Connecting to the live feed…';

  @override
  String get sessionReconnecting =>
      'Connection lost — reconnecting. Live updates are paused.';

  @override
  String get sessionDisconnected => 'Disconnected — live updates are paused.';

  @override
  String get sessionReconnect => 'Reconnect';

  @override
  String get sessionActivity => 'Activity';

  @override
  String get sessionLeaveTooltip => 'Leave session';

  @override
  String get sessionEndedBanner => 'Session ended — event stream stopped.';

  @override
  String get sessionWaitingEvents => 'Waiting for events…';

  @override
  String get sessionStateStale => 'Agent stopped reporting';

  @override
  String get sessionStateLeft => 'Left';

  @override
  String get sessionStateDeclined => 'Declined';

  @override
  String get sessionStateNotJoined => 'Not joined';

  @override
  String get sessionStateInSession => 'In session';

  @override
  String get sessionStateUnknown => 'Unknown';

  @override
  String sessionStudentsCount(int joined, int total) {
    return 'Students ($joined/$total in session)';
  }

  @override
  String get sessionTamperTooltip => 'Tampering detected';

  @override
  String get sessionPendingRequests => 'Pending requests';

  @override
  String sessionRequesters(int count, String names) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count students — $names',
      one: '1 student — $names',
    );
    return '$_temp0';
  }

  @override
  String get sessionMoreApprovalOptions => 'More approval options';

  @override
  String get sessionNotYours =>
      'This session isn\'t available to you. Only the teacher who started it can open it.';

  @override
  String get sessionApproveWholeClass => 'Approve for whole class';

  @override
  String get sessionSummary => 'Session summary';

  @override
  String get sessionAllowedBundles => 'Allowed bundles';

  @override
  String get sessionNoBundles => 'No bundles available yet.';

  @override
  String get sessionJoinCode => 'Join code';

  @override
  String get sessionCopyCode => 'Copy code';

  @override
  String get bundlesLoadListError =>
      'Could not load bundles. Please try again.';

  @override
  String get bundlesLoadOneError =>
      'Could not load this bundle. Please try again.';

  @override
  String get bundlesNameRequired => 'Name is required.';

  @override
  String get bundlesEntryValueRequired => 'Every entry must have a value.';

  @override
  String get bundlesEntryAtLeastOne => 'At least one entry is required.';

  @override
  String get bundlesSaveError => 'Could not save the bundle. Please try again.';

  @override
  String get bundlesNameTaken =>
      'A bundle with that name already exists. Choose another name.';

  @override
  String get bundlesArchiveTitle => 'Archive bundle?';

  @override
  String bundlesArchiveBody(String name) {
    return '\"$name\" will be hidden from the picker. Past sessions referencing it stay intact. You can restore it later by editing.';
  }

  @override
  String get bundlesArchive => 'Archive';

  @override
  String get bundlesArchiveError =>
      'Could not archive the bundle. Please try again.';

  @override
  String get bundlesDeleteTitle => 'Delete bundle?';

  @override
  String bundlesDeleteBody(String name) {
    return '\"$name\" will be permanently removed. This cannot be undone. Available because no session has ever used this bundle.';
  }

  @override
  String get bundlesDeleteError =>
      'Could not delete the bundle. Please try again.';

  @override
  String get bundlesDeleteUsedError =>
      'A session has used this bundle, so it can\'t be deleted. Archive it instead.';

  @override
  String bundlesSaveFailedFor(String name) {
    return 'Could not save \"$name\".';
  }

  @override
  String bundlesArchiveFailedFor(String name) {
    return 'Could not archive \"$name\".';
  }

  @override
  String bundlesDeleteFailedFor(String name) {
    return 'Could not delete \"$name\".';
  }

  @override
  String get bundlesTryAgain => 'Please try again.';

  @override
  String bundlesNameTakenBy(String name) {
    return 'Another bundle is already called \"$name\".';
  }

  @override
  String get bundlesUsedArchiveInstead =>
      'A session has used it. Archive it instead.';

  @override
  String get bundlesReopen => 'Reopen';

  @override
  String bundlesTestNoMatch(String probe) {
    return 'No entry matches \"$probe\".';
  }

  @override
  String bundlesTestMatch(String value, String kind, String matchType) {
    return 'Matches \"$value\" ($kind / $matchType).';
  }

  @override
  String get bundlesNothingToExport =>
      'Nothing to export — the catalogue is empty.';

  @override
  String bundlesExported(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count bundles',
      one: '1 bundle',
    );
    return 'Exported $_temp0.';
  }

  @override
  String get bundlesExportError =>
      'Could not export the bundles. Please try again.';

  @override
  String bundlesImported(int count, int created, int updated) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count bundles',
      one: '1 bundle',
    );
    return 'Imported $_temp0 ($created created, $updated updated).';
  }

  @override
  String bundlesImportedWithFailures(int count, int created, int updated) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count failures',
      one: '1 failure',
    );
    return 'Imported with $_temp0 ($created created, $updated updated)';
  }

  @override
  String get bundlesImportError =>
      'Could not import the bundles. Please try again.';

  @override
  String bundlesImportOneError(String name) {
    return '\"$name\" could not be saved.';
  }

  @override
  String get bundlesImportRejected => 'Import rejected';

  @override
  String get bundlesAdminRequired => 'Admin access required.';

  @override
  String get bundlesCatalogue => 'Catalogue';

  @override
  String get bundlesShowArchived => 'Show archived bundles';

  @override
  String get bundlesNewBundle => 'New bundle';

  @override
  String get bundlesExportAll => 'Export all';

  @override
  String get bundlesNoBundles => 'No bundles.';

  @override
  String get bundlesSelectOrNew => 'Select a bundle, or start a new one.';

  @override
  String get bundlesNameLabel => 'Name';

  @override
  String get bundlesDomains => 'Domains';

  @override
  String get bundlesApps => 'Apps';

  @override
  String get bundlesExportTooltip => 'Download this bundle as a JSON file.';

  @override
  String get bundlesExport => 'Export';

  @override
  String get bundlesEditsFooter =>
      'Edits take effect at the next session start.';

  @override
  String get bundlesDeleteTooltip =>
      'Permanently delete — this bundle has never been used in a session.';

  @override
  String get bundlesArchiveTooltip =>
      'Hide from the picker. Hard delete is not possible because this bundle has been used in past sessions.';

  @override
  String get bundlesTest => 'Test';

  @override
  String get bundlesTestHint =>
      'Paste a URL or process name to see whether the current draft matches.';

  @override
  String get bundlesTestFieldHint => 'e.g. https://www.geogebra.org/calc';

  @override
  String get bundlesCheck => 'Check';

  @override
  String get bundlesValSignedPublisherDomain =>
      'SignedPublisher is not valid for a domain.';

  @override
  String bundlesValInvalidDomain(String value) {
    return '\"$value\" is not a valid domain.';
  }

  @override
  String bundlesValMatchTypeApp(String matchType) {
    return '$matchType is not valid for an app.';
  }

  @override
  String bundlesValProcessPath(String value) {
    return 'Process name \"$value\" must not include a path.';
  }

  @override
  String bundlesValProcessExe(String value) {
    return 'Process name \"$value\" must not include the .exe suffix.';
  }

  @override
  String get bundlesKindDomain => 'Domain';

  @override
  String get bundlesKindApp => 'App';

  @override
  String get bundlesMatchExact => 'Exact';

  @override
  String get bundlesMatchWildcard => 'Wildcard';

  @override
  String get bundlesMatchSuffix => 'Suffix';

  @override
  String get bundlesMatchSignedPublisher => 'SignedPublisher';

  @override
  String get bundlesNoDomainEntries => 'No domains entries.';

  @override
  String get bundlesNoAppEntries => 'No apps entries.';

  @override
  String get bundlesDomainHint => 'e.g. *.geogebra.org';

  @override
  String get bundlesAppHint => 'e.g. msedge';

  @override
  String get bundlesRemoveEntry => 'Remove entry';

  @override
  String get addStudentSearchUnavailable => 'Directory search unavailable.';

  @override
  String get addStudentLabel => 'Add student';

  @override
  String get addStudentSearchDisabled => 'Search disabled';

  @override
  String get addStudentSearchByName => 'Search by name';

  @override
  String get addStudentNoMatches => 'No matches.';

  @override
  String get adminsLoadError => 'Could not load admins. Please try again.';

  @override
  String get adminsSearchError => 'Search failed. Please try again.';

  @override
  String adminsPromoteError(String name) {
    return 'Could not promote $name. Please try again.';
  }

  @override
  String get adminsRemoveTitle => 'Remove admin?';

  @override
  String adminsRemoveBody(String name) {
    return '\"$name\" will lose admin access and return to a regular teacher/student role. They keep their account; you can promote them again later.';
  }

  @override
  String get adminsLastAdminError =>
      'Can’t remove the last admin. Promote another user first.';

  @override
  String adminsRemoveError(String name) {
    return 'Could not remove $name. Please try again.';
  }

  @override
  String get adminsCurrentAdmins => 'Current admins';

  @override
  String get adminsAddAdmin => 'Add admin';

  @override
  String get adminsAddHint =>
      'Search a user who has signed in to the dashboard, then promote them.';

  @override
  String get adminsSearchByName => 'Search by name';

  @override
  String get adminsSearching => 'Searching…';

  @override
  String get adminsNoCandidates =>
      'No signed-in user matches. They must sign in to the dashboard at least once before they can be promoted.';

  @override
  String get adminsNoAdmins => 'No admins.';

  @override
  String get schoolsLoadError => 'Could not load schools. Please try again.';

  @override
  String schoolsUpdateError(String name) {
    return 'Could not update $name. Please try again.';
  }

  @override
  String get schoolsHeader => 'Schools';

  @override
  String get schoolsIntro =>
      'Active schools appear in the Classes school selector for teachers. Deactivate the ones your teachers don’t need.';

  @override
  String get schoolsEmpty =>
      'No schools found. Schools come from your directory; once teachers belong to one, it will appear here.';

  @override
  String get historyLoadError =>
      'Could not load past sessions. Please try again.';

  @override
  String get historyEmpty =>
      'No past sessions yet. Sessions appear here after you end them.';

  @override
  String get historyPastSessions => 'Past sessions';

  @override
  String get historySessionFallback => 'Session';

  @override
  String get pastLoadError =>
      'Could not load this past session. Please try again.';

  @override
  String get pastNotAvailable => 'Session not available.';

  @override
  String get pastSessionFallback => 'Session';

  @override
  String get pastBundlesUsed => 'Bundles used';

  @override
  String get pastParticipants => 'Participants';

  @override
  String get pastActivitySummary => 'Activity summary';

  @override
  String get pastApprovedExceptions => 'Approved exceptions';

  @override
  String get pastUnapprovedRequests => 'Unapproved requests';

  @override
  String get pastEventLog => 'Event log';

  @override
  String pastStatusDeclinedAt(String time) {
    return 'declined at $time';
  }

  @override
  String get pastStatusNeverJoined => 'never joined';

  @override
  String pastStatusJoinedAt(String time) {
    return 'joined $time';
  }

  @override
  String pastStatusJoinedLeft(String joined, String left) {
    return 'joined $joined  ·  left $left';
  }

  @override
  String get pastNoEventDetail => 'No event detail retained for this session.';
}
