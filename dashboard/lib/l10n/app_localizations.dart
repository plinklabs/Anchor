import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:intl/intl.dart' as intl;

import 'app_localizations_en.dart';
import 'app_localizations_nl.dart';

// ignore_for_file: type=lint

/// Callers can lookup localized strings with an instance of AppLocalizations
/// returned by `AppLocalizations.of(context)`.
///
/// Applications need to include `AppLocalizations.delegate()` in their app's
/// `localizationDelegates` list, and the locales they support in the app's
/// `supportedLocales` list. For example:
///
/// ```dart
/// import 'l10n/app_localizations.dart';
///
/// return MaterialApp(
///   localizationsDelegates: AppLocalizations.localizationsDelegates,
///   supportedLocales: AppLocalizations.supportedLocales,
///   home: MyApplicationHome(),
/// );
/// ```
///
/// ## Update pubspec.yaml
///
/// Please make sure to update your pubspec.yaml to include the following
/// packages:
///
/// ```yaml
/// dependencies:
///   # Internationalization support.
///   flutter_localizations:
///     sdk: flutter
///   intl: any # Use the pinned version from flutter_localizations
///
///   # Rest of dependencies
/// ```
///
/// ## iOS Applications
///
/// iOS applications define key application metadata, including supported
/// locales, in an Info.plist file that is built into the application bundle.
/// To configure the locales supported by your app, you’ll need to edit this
/// file.
///
/// First, open your project’s ios/Runner.xcworkspace Xcode workspace file.
/// Then, in the Project Navigator, open the Info.plist file under the Runner
/// project’s Runner folder.
///
/// Next, select the Information Property List item, select Add Item from the
/// Editor menu, then select Localizations from the pop-up menu.
///
/// Select and expand the newly-created Localizations item then, for each
/// locale your application supports, add a new item and select the locale
/// you wish to add from the pop-up menu in the Value field. This list should
/// be consistent with the languages listed in the AppLocalizations.supportedLocales
/// property.
abstract class AppLocalizations {
  AppLocalizations(String locale)
    : localeName = intl.Intl.canonicalizedLocale(locale.toString());

  final String localeName;

  static AppLocalizations of(BuildContext context) {
    return Localizations.of<AppLocalizations>(context, AppLocalizations)!;
  }

  static const LocalizationsDelegate<AppLocalizations> delegate =
      _AppLocalizationsDelegate();

  /// A list of this localizations delegate along with the default localizations
  /// delegates.
  ///
  /// Returns a list of localizations delegates containing this delegate along with
  /// GlobalMaterialLocalizations.delegate, GlobalCupertinoLocalizations.delegate,
  /// and GlobalWidgetsLocalizations.delegate.
  ///
  /// Additional delegates can be added by appending to this list in
  /// MaterialApp. This list does not have to be used at all if a custom list
  /// of delegates is preferred or required.
  static const List<LocalizationsDelegate<dynamic>> localizationsDelegates =
      <LocalizationsDelegate<dynamic>>[
        delegate,
        GlobalMaterialLocalizations.delegate,
        GlobalCupertinoLocalizations.delegate,
        GlobalWidgetsLocalizations.delegate,
      ];

  /// A list of this localizations delegate's supported locales.
  static const List<Locale> supportedLocales = <Locale>[
    Locale('en'),
    Locale('nl'),
  ];

  /// No description provided for @actionCancel.
  ///
  /// In en, this message translates to:
  /// **'Cancel'**
  String get actionCancel;

  /// No description provided for @actionDelete.
  ///
  /// In en, this message translates to:
  /// **'Delete'**
  String get actionDelete;

  /// No description provided for @actionRemove.
  ///
  /// In en, this message translates to:
  /// **'Remove'**
  String get actionRemove;

  /// No description provided for @actionAdd.
  ///
  /// In en, this message translates to:
  /// **'Add'**
  String get actionAdd;

  /// No description provided for @actionSave.
  ///
  /// In en, this message translates to:
  /// **'Save'**
  String get actionSave;

  /// No description provided for @actionCreate.
  ///
  /// In en, this message translates to:
  /// **'Create'**
  String get actionCreate;

  /// No description provided for @actionImport.
  ///
  /// In en, this message translates to:
  /// **'Import'**
  String get actionImport;

  /// No description provided for @actionClose.
  ///
  /// In en, this message translates to:
  /// **'Close'**
  String get actionClose;

  /// No description provided for @actionRetry.
  ///
  /// In en, this message translates to:
  /// **'Retry'**
  String get actionRetry;

  /// No description provided for @actionLoadMore.
  ///
  /// In en, this message translates to:
  /// **'Load more'**
  String get actionLoadMore;

  /// No description provided for @actionApprove.
  ///
  /// In en, this message translates to:
  /// **'Approve'**
  String get actionApprove;

  /// No description provided for @badgeLive.
  ///
  /// In en, this message translates to:
  /// **'Live'**
  String get badgeLive;

  /// No description provided for @badgeEnded.
  ///
  /// In en, this message translates to:
  /// **'Ended'**
  String get badgeEnded;

  /// No description provided for @badgeArchived.
  ///
  /// In en, this message translates to:
  /// **'Archived'**
  String get badgeArchived;

  /// No description provided for @statusActive.
  ///
  /// In en, this message translates to:
  /// **'Active'**
  String get statusActive;

  /// No description provided for @statusInactive.
  ///
  /// In en, this message translates to:
  /// **'Inactive'**
  String get statusInactive;

  /// No description provided for @commonLoading.
  ///
  /// In en, this message translates to:
  /// **'Loading…'**
  String get commonLoading;

  /// No description provided for @commonNone.
  ///
  /// In en, this message translates to:
  /// **'(none)'**
  String get commonNone;

  /// No description provided for @loginEyebrow.
  ///
  /// In en, this message translates to:
  /// **'Anchor for teachers'**
  String get loginEyebrow;

  /// No description provided for @loginHeadline.
  ///
  /// In en, this message translates to:
  /// **'Ready when your class is.'**
  String get loginHeadline;

  /// No description provided for @loginSubtitle.
  ///
  /// In en, this message translates to:
  /// **'Sign in with your school account to start a focus session for a class.'**
  String get loginSubtitle;

  /// No description provided for @loginSignInButton.
  ///
  /// In en, this message translates to:
  /// **'Sign in with Microsoft'**
  String get loginSignInButton;

  /// No description provided for @loginTimeoutError.
  ///
  /// In en, this message translates to:
  /// **'Signing in is taking longer than expected. Please try again.'**
  String get loginTimeoutError;

  /// No description provided for @shellNavHome.
  ///
  /// In en, this message translates to:
  /// **'Home'**
  String get shellNavHome;

  /// No description provided for @shellNavClasses.
  ///
  /// In en, this message translates to:
  /// **'Classes'**
  String get shellNavClasses;

  /// No description provided for @shellNavHistory.
  ///
  /// In en, this message translates to:
  /// **'History'**
  String get shellNavHistory;

  /// No description provided for @shellNavAdmin.
  ///
  /// In en, this message translates to:
  /// **'Admin'**
  String get shellNavAdmin;

  /// App-bar sign-out label. Stored pre-uppercased to match the source treatment.
  ///
  /// In en, this message translates to:
  /// **'SIGN OUT'**
  String get shellSignOut;

  /// No description provided for @shellSectionPastSessions.
  ///
  /// In en, this message translates to:
  /// **'Past sessions'**
  String get shellSectionPastSessions;

  /// No description provided for @shellSectionLiveSession.
  ///
  /// In en, this message translates to:
  /// **'Live session'**
  String get shellSectionLiveSession;

  /// No description provided for @shellSectionPastSession.
  ///
  /// In en, this message translates to:
  /// **'Past session'**
  String get shellSectionPastSession;

  /// No description provided for @adminNavBundles.
  ///
  /// In en, this message translates to:
  /// **'Bundles'**
  String get adminNavBundles;

  /// No description provided for @adminNavAdmins.
  ///
  /// In en, this message translates to:
  /// **'Admins'**
  String get adminNavAdmins;

  /// No description provided for @adminNavSchools.
  ///
  /// In en, this message translates to:
  /// **'Schools'**
  String get adminNavSchools;

  /// Shown when an authenticated user lacks the Teacher role (a 403).
  ///
  /// In en, this message translates to:
  /// **'Your account isn\'t set up as a teacher yet. Ask an administrator to grant access.'**
  String get apiError403;

  /// No description provided for @homeLoadError.
  ///
  /// In en, this message translates to:
  /// **'Could not load start-session data. Please try again.'**
  String get homeLoadError;

  /// No description provided for @homeStartError.
  ///
  /// In en, this message translates to:
  /// **'Failed to start session. Please try again.'**
  String get homeStartError;

  /// No description provided for @homeEndError.
  ///
  /// In en, this message translates to:
  /// **'Failed to end session. Please try again.'**
  String get homeEndError;

  /// No description provided for @homeActiveSessionFallback.
  ///
  /// In en, this message translates to:
  /// **'Active session'**
  String get homeActiveSessionFallback;

  /// No description provided for @homeStillRunning.
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{Still running} other{Still running — {count} sessions}}'**
  String homeStillRunning(int count);

  /// No description provided for @homeHeadline.
  ///
  /// In en, this message translates to:
  /// **'Start a focus session.'**
  String get homeHeadline;

  /// No description provided for @homeSubtitle.
  ///
  /// In en, this message translates to:
  /// **'Pick a class and start. Students join with the code shown on the session screen.'**
  String get homeSubtitle;

  /// No description provided for @homeNoClasses.
  ///
  /// In en, this message translates to:
  /// **'No classes assigned to you yet.'**
  String get homeNoClasses;

  /// No description provided for @homeYourDepartment.
  ///
  /// In en, this message translates to:
  /// **'YOUR DEPARTMENT · {department}'**
  String homeYourDepartment(String department);

  /// No description provided for @homeClassLabel.
  ///
  /// In en, this message translates to:
  /// **'Class'**
  String get homeClassLabel;

  /// No description provided for @homeSelectClass.
  ///
  /// In en, this message translates to:
  /// **'Select a class'**
  String get homeSelectClass;

  /// No description provided for @homeStartSessionFor.
  ///
  /// In en, this message translates to:
  /// **'Start session for {name}'**
  String homeStartSessionFor(String name);

  /// No description provided for @homeStartedAt.
  ///
  /// In en, this message translates to:
  /// **'STARTED {time}'**
  String homeStartedAt(String time);

  /// No description provided for @homeResume.
  ///
  /// In en, this message translates to:
  /// **'Resume'**
  String get homeResume;

  /// No description provided for @homeEnd.
  ///
  /// In en, this message translates to:
  /// **'End'**
  String get homeEnd;

  /// No description provided for @classesLoadError.
  ///
  /// In en, this message translates to:
  /// **'Could not load classes. Please try again.'**
  String get classesLoadError;

  /// No description provided for @classesLoadSchoolsError.
  ///
  /// In en, this message translates to:
  /// **'Couldn\'t load schools. The selector may be incomplete.'**
  String get classesLoadSchoolsError;

  /// No description provided for @classesLoadRosterError.
  ///
  /// In en, this message translates to:
  /// **'Could not load roster. Please try again.'**
  String get classesLoadRosterError;

  /// No description provided for @classesDeleteClass.
  ///
  /// In en, this message translates to:
  /// **'Delete class'**
  String get classesDeleteClass;

  /// No description provided for @classesDeleteClassBody.
  ///
  /// In en, this message translates to:
  /// **'Delete {name} ({year})? This removes the class and its roster. Classes with past sessions cannot be deleted.'**
  String classesDeleteClassBody(String name, String year);

  /// No description provided for @classesDeleteError.
  ///
  /// In en, this message translates to:
  /// **'Could not delete class. Please try again.'**
  String get classesDeleteError;

  /// No description provided for @classesSaveCodesError.
  ///
  /// In en, this message translates to:
  /// **'Could not save school + code. Please try again.'**
  String get classesSaveCodesError;

  /// No description provided for @classesAddMemberError.
  ///
  /// In en, this message translates to:
  /// **'Failed to add member. Please try again.'**
  String get classesAddMemberError;

  /// No description provided for @classesRemoveMemberTitle.
  ///
  /// In en, this message translates to:
  /// **'Remove member'**
  String get classesRemoveMemberTitle;

  /// No description provided for @classesRemoveMemberBody.
  ///
  /// In en, this message translates to:
  /// **'Remove {name} from {className}? They will stop receiving session broadcasts on the next session start.'**
  String classesRemoveMemberBody(String name, String className);

  /// No description provided for @classesRemoveMemberError.
  ///
  /// In en, this message translates to:
  /// **'Failed to remove member. Please try again.'**
  String get classesRemoveMemberError;

  /// No description provided for @classesCsvNoRows.
  ///
  /// In en, this message translates to:
  /// **'No valid rows in CSV.'**
  String get classesCsvNoRows;

  /// No description provided for @classesImportError.
  ///
  /// In en, this message translates to:
  /// **'Import failed. Please try again.'**
  String get classesImportError;

  /// No description provided for @classesGraphImportError.
  ///
  /// In en, this message translates to:
  /// **'Populate from Graph failed. Please try again.'**
  String get classesGraphImportError;

  /// No description provided for @classesPickClass.
  ///
  /// In en, this message translates to:
  /// **'Pick a class on the left.'**
  String get classesPickClass;

  /// No description provided for @classesListHeader.
  ///
  /// In en, this message translates to:
  /// **'Classes'**
  String get classesListHeader;

  /// No description provided for @classesNewClass.
  ///
  /// In en, this message translates to:
  /// **'New class'**
  String get classesNewClass;

  /// No description provided for @classesEmpty.
  ///
  /// In en, this message translates to:
  /// **'No classes you teach. Create one with \"New class\".'**
  String get classesEmpty;

  /// No description provided for @classesSetScopeFirst.
  ///
  /// In en, this message translates to:
  /// **'Set school + code first'**
  String get classesSetScopeFirst;

  /// No description provided for @classesImportCsv.
  ///
  /// In en, this message translates to:
  /// **'Import CSV'**
  String get classesImportCsv;

  /// No description provided for @classesPopulateFromGraph.
  ///
  /// In en, this message translates to:
  /// **'Populate from Graph'**
  String get classesPopulateFromGraph;

  /// No description provided for @classesSchoolLabel.
  ///
  /// In en, this message translates to:
  /// **'School'**
  String get classesSchoolLabel;

  /// No description provided for @classesClassCodeLabel.
  ///
  /// In en, this message translates to:
  /// **'Class code'**
  String get classesClassCodeLabel;

  /// No description provided for @classesHint3A.
  ///
  /// In en, this message translates to:
  /// **'e.g. 3A'**
  String get classesHint3A;

  /// No description provided for @classesNoMembers.
  ///
  /// In en, this message translates to:
  /// **'No members yet.'**
  String get classesNoMembers;

  /// No description provided for @classesDisplayName.
  ///
  /// In en, this message translates to:
  /// **'Display name'**
  String get classesDisplayName;

  /// No description provided for @classesRole.
  ///
  /// In en, this message translates to:
  /// **'Role'**
  String get classesRole;

  /// No description provided for @classesImportAdded.
  ///
  /// In en, this message translates to:
  /// **'{count} added'**
  String classesImportAdded(int count);

  /// No description provided for @classesImportAlready.
  ///
  /// In en, this message translates to:
  /// **'{count} already member'**
  String classesImportAlready(int count);

  /// No description provided for @classesImportUnresolved.
  ///
  /// In en, this message translates to:
  /// **'{count} unresolved'**
  String classesImportUnresolved(int count);

  /// No description provided for @classesImportWrongSchool.
  ///
  /// In en, this message translates to:
  /// **'{count} wrong school'**
  String classesImportWrongSchool(int count);

  /// No description provided for @classesBlank.
  ///
  /// In en, this message translates to:
  /// **'(blank)'**
  String get classesBlank;

  /// No description provided for @classesCreateError.
  ///
  /// In en, this message translates to:
  /// **'Could not create class. Please try again.'**
  String get classesCreateError;

  /// No description provided for @classesNameLabel.
  ///
  /// In en, this message translates to:
  /// **'Name'**
  String get classesNameLabel;

  /// No description provided for @classesSchoolYearLabel.
  ///
  /// In en, this message translates to:
  /// **'School year'**
  String get classesSchoolYearLabel;

  /// No description provided for @classesSchoolYearHint.
  ///
  /// In en, this message translates to:
  /// **'e.g. 2025-2026'**
  String get classesSchoolYearHint;

  /// No description provided for @classesSchoolOptionalLabel.
  ///
  /// In en, this message translates to:
  /// **'School (optional)'**
  String get classesSchoolOptionalLabel;

  /// No description provided for @classesClassCodeOptionalLabel.
  ///
  /// In en, this message translates to:
  /// **'Class code (optional)'**
  String get classesClassCodeOptionalLabel;

  /// No description provided for @classesCsvDialogTitle.
  ///
  /// In en, this message translates to:
  /// **'Import roster from CSV'**
  String get classesCsvDialogTitle;

  /// No description provided for @classesCsvDialogBody.
  ///
  /// In en, this message translates to:
  /// **'Paste a CSV with a header row. Required column: upn (the user principal name, e.g. student@school.be). Names are looked up in the directory automatically.'**
  String get classesCsvDialogBody;

  /// No description provided for @classesCsvHint.
  ///
  /// In en, this message translates to:
  /// **'upn\nalice@school.be\nbob@school.be\n...'**
  String get classesCsvHint;

  /// No description provided for @classesCsvEmpty.
  ///
  /// In en, this message translates to:
  /// **'CSV is empty.'**
  String get classesCsvEmpty;

  /// No description provided for @classesCsvHeaderMissing.
  ///
  /// In en, this message translates to:
  /// **'Header must include upn.'**
  String get classesCsvHeaderMissing;

  /// No description provided for @sessionUpdateBundlesError.
  ///
  /// In en, this message translates to:
  /// **'Failed to update bundles: {error}'**
  String sessionUpdateBundlesError(String error);

  /// No description provided for @sessionApproveError.
  ///
  /// In en, this message translates to:
  /// **'Approve failed: {error}'**
  String sessionApproveError(String error);

  /// No description provided for @sessionCopiedCode.
  ///
  /// In en, this message translates to:
  /// **'Copied {code}'**
  String sessionCopiedCode(String code);

  /// No description provided for @sessionConnectError.
  ///
  /// In en, this message translates to:
  /// **'Could not connect to live stream: {error}'**
  String sessionConnectError(String error);

  /// No description provided for @sessionEndError.
  ///
  /// In en, this message translates to:
  /// **'Failed to end session: {error}'**
  String sessionEndError(String error);

  /// No description provided for @sessionLeaveTitle.
  ///
  /// In en, this message translates to:
  /// **'Leave this session?'**
  String get sessionLeaveTitle;

  /// No description provided for @sessionLeaveBody.
  ///
  /// In en, this message translates to:
  /// **'This session is still running and students stay enforced. End it for everyone, or leave it running and come back to it later from the home screen?'**
  String get sessionLeaveBody;

  /// No description provided for @sessionLeaveRunning.
  ///
  /// In en, this message translates to:
  /// **'Leave running'**
  String get sessionLeaveRunning;

  /// No description provided for @sessionEndSession.
  ///
  /// In en, this message translates to:
  /// **'End session'**
  String get sessionEndSession;

  /// No description provided for @sessionTitleFallback.
  ///
  /// In en, this message translates to:
  /// **'Session'**
  String get sessionTitleFallback;

  /// No description provided for @sessionTitleSpec.
  ///
  /// In en, this message translates to:
  /// **'Session {datetime}'**
  String sessionTitleSpec(String datetime);

  /// No description provided for @sessionHeadlineFallback.
  ///
  /// In en, this message translates to:
  /// **'Live session'**
  String get sessionHeadlineFallback;

  /// No description provided for @sessionConnecting.
  ///
  /// In en, this message translates to:
  /// **'Connecting to the live feed…'**
  String get sessionConnecting;

  /// No description provided for @sessionActivity.
  ///
  /// In en, this message translates to:
  /// **'Activity'**
  String get sessionActivity;

  /// No description provided for @sessionLeaveTooltip.
  ///
  /// In en, this message translates to:
  /// **'Leave session'**
  String get sessionLeaveTooltip;

  /// No description provided for @sessionEndedBanner.
  ///
  /// In en, this message translates to:
  /// **'Session ended — event stream stopped.'**
  String get sessionEndedBanner;

  /// No description provided for @sessionWaitingEvents.
  ///
  /// In en, this message translates to:
  /// **'Waiting for events…'**
  String get sessionWaitingEvents;

  /// No description provided for @sessionStateStale.
  ///
  /// In en, this message translates to:
  /// **'Agent stopped reporting'**
  String get sessionStateStale;

  /// No description provided for @sessionStateLeft.
  ///
  /// In en, this message translates to:
  /// **'Left'**
  String get sessionStateLeft;

  /// No description provided for @sessionStateDeclined.
  ///
  /// In en, this message translates to:
  /// **'Declined'**
  String get sessionStateDeclined;

  /// No description provided for @sessionStateNotJoined.
  ///
  /// In en, this message translates to:
  /// **'Not joined'**
  String get sessionStateNotJoined;

  /// No description provided for @sessionStateInSession.
  ///
  /// In en, this message translates to:
  /// **'In session'**
  String get sessionStateInSession;

  /// No description provided for @sessionStateUnknown.
  ///
  /// In en, this message translates to:
  /// **'Unknown'**
  String get sessionStateUnknown;

  /// No description provided for @sessionStudentsCount.
  ///
  /// In en, this message translates to:
  /// **'Students ({joined}/{total} in session)'**
  String sessionStudentsCount(int joined, int total);

  /// No description provided for @sessionTamperTooltip.
  ///
  /// In en, this message translates to:
  /// **'Tampering detected'**
  String get sessionTamperTooltip;

  /// No description provided for @sessionPendingRequests.
  ///
  /// In en, this message translates to:
  /// **'Pending requests'**
  String get sessionPendingRequests;

  /// No description provided for @sessionRequesters.
  ///
  /// In en, this message translates to:
  /// **'{count, plural, =1{1 student — {names}} other{{count} students — {names}}}'**
  String sessionRequesters(int count, String names);

  /// No description provided for @sessionMoreApprovalOptions.
  ///
  /// In en, this message translates to:
  /// **'More approval options'**
  String get sessionMoreApprovalOptions;

  /// No description provided for @sessionApproveWholeClass.
  ///
  /// In en, this message translates to:
  /// **'Approve for whole class'**
  String get sessionApproveWholeClass;

  /// No description provided for @sessionSummary.
  ///
  /// In en, this message translates to:
  /// **'Session summary'**
  String get sessionSummary;

  /// No description provided for @sessionAllowedBundles.
  ///
  /// In en, this message translates to:
  /// **'Allowed bundles'**
  String get sessionAllowedBundles;

  /// No description provided for @sessionNoBundles.
  ///
  /// In en, this message translates to:
  /// **'No bundles available yet.'**
  String get sessionNoBundles;

  /// No description provided for @sessionJoinCode.
  ///
  /// In en, this message translates to:
  /// **'Join code'**
  String get sessionJoinCode;

  /// No description provided for @sessionCopyCode.
  ///
  /// In en, this message translates to:
  /// **'Copy code'**
  String get sessionCopyCode;

  /// No description provided for @bundlesLoadError.
  ///
  /// In en, this message translates to:
  /// **'Could not load: {error}'**
  String bundlesLoadError(String error);

  /// No description provided for @bundlesLoadListError.
  ///
  /// In en, this message translates to:
  /// **'Could not load bundles: {error}'**
  String bundlesLoadListError(String error);

  /// No description provided for @bundlesLoadOneError.
  ///
  /// In en, this message translates to:
  /// **'Failed to load bundle: {error}'**
  String bundlesLoadOneError(String error);

  /// No description provided for @bundlesNameRequired.
  ///
  /// In en, this message translates to:
  /// **'Name is required.'**
  String get bundlesNameRequired;

  /// No description provided for @bundlesEntryValueRequired.
  ///
  /// In en, this message translates to:
  /// **'Every entry must have a value.'**
  String get bundlesEntryValueRequired;

  /// No description provided for @bundlesEntryAtLeastOne.
  ///
  /// In en, this message translates to:
  /// **'At least one entry is required.'**
  String get bundlesEntryAtLeastOne;

  /// No description provided for @bundlesSaveError.
  ///
  /// In en, this message translates to:
  /// **'Save failed: {error}'**
  String bundlesSaveError(String error);

  /// No description provided for @bundlesArchiveTitle.
  ///
  /// In en, this message translates to:
  /// **'Archive bundle?'**
  String get bundlesArchiveTitle;

  /// No description provided for @bundlesArchiveBody.
  ///
  /// In en, this message translates to:
  /// **'\"{name}\" will be hidden from the picker. Past sessions referencing it stay intact. You can restore it later by editing.'**
  String bundlesArchiveBody(String name);

  /// No description provided for @bundlesArchive.
  ///
  /// In en, this message translates to:
  /// **'Archive'**
  String get bundlesArchive;

  /// No description provided for @bundlesArchiveError.
  ///
  /// In en, this message translates to:
  /// **'Archive failed: {error}'**
  String bundlesArchiveError(String error);

  /// No description provided for @bundlesDeleteTitle.
  ///
  /// In en, this message translates to:
  /// **'Delete bundle?'**
  String get bundlesDeleteTitle;

  /// No description provided for @bundlesDeleteBody.
  ///
  /// In en, this message translates to:
  /// **'\"{name}\" will be permanently removed. This cannot be undone. Available because no session has ever used this bundle.'**
  String bundlesDeleteBody(String name);

  /// No description provided for @bundlesDeleteError.
  ///
  /// In en, this message translates to:
  /// **'Delete failed: {error}'**
  String bundlesDeleteError(String error);

  /// No description provided for @bundlesTestNoMatch.
  ///
  /// In en, this message translates to:
  /// **'No entry matches \"{probe}\".'**
  String bundlesTestNoMatch(String probe);

  /// No description provided for @bundlesTestMatch.
  ///
  /// In en, this message translates to:
  /// **'Matches \"{value}\" ({kind} / {matchType}).'**
  String bundlesTestMatch(String value, String kind, String matchType);

  /// No description provided for @bundlesNothingToExport.
  ///
  /// In en, this message translates to:
  /// **'Nothing to export — the catalogue is empty.'**
  String get bundlesNothingToExport;

  /// No description provided for @bundlesExported.
  ///
  /// In en, this message translates to:
  /// **'Exported {count, plural, =1{1 bundle} other{{count} bundles}}.'**
  String bundlesExported(int count);

  /// No description provided for @bundlesExportError.
  ///
  /// In en, this message translates to:
  /// **'Export failed: {error}'**
  String bundlesExportError(String error);

  /// No description provided for @bundlesImported.
  ///
  /// In en, this message translates to:
  /// **'Imported {count, plural, =1{1 bundle} other{{count} bundles}} ({created} created, {updated} updated).'**
  String bundlesImported(int count, int created, int updated);

  /// No description provided for @bundlesImportedWithFailures.
  ///
  /// In en, this message translates to:
  /// **'Imported with {count, plural, =1{1 failure} other{{count} failures}} ({created} created, {updated} updated)'**
  String bundlesImportedWithFailures(int count, int created, int updated);

  /// No description provided for @bundlesImportError.
  ///
  /// In en, this message translates to:
  /// **'Import failed: {error}'**
  String bundlesImportError(String error);

  /// No description provided for @bundlesImportRejected.
  ///
  /// In en, this message translates to:
  /// **'Import rejected'**
  String get bundlesImportRejected;

  /// No description provided for @bundlesAdminRequired.
  ///
  /// In en, this message translates to:
  /// **'Admin access required.'**
  String get bundlesAdminRequired;

  /// No description provided for @bundlesCatalogue.
  ///
  /// In en, this message translates to:
  /// **'Catalogue'**
  String get bundlesCatalogue;

  /// No description provided for @bundlesShowArchived.
  ///
  /// In en, this message translates to:
  /// **'Show archived bundles'**
  String get bundlesShowArchived;

  /// No description provided for @bundlesNewBundle.
  ///
  /// In en, this message translates to:
  /// **'New bundle'**
  String get bundlesNewBundle;

  /// No description provided for @bundlesExportAll.
  ///
  /// In en, this message translates to:
  /// **'Export all'**
  String get bundlesExportAll;

  /// No description provided for @bundlesNoBundles.
  ///
  /// In en, this message translates to:
  /// **'No bundles.'**
  String get bundlesNoBundles;

  /// No description provided for @bundlesSelectOrNew.
  ///
  /// In en, this message translates to:
  /// **'Select a bundle, or start a new one.'**
  String get bundlesSelectOrNew;

  /// No description provided for @bundlesNameLabel.
  ///
  /// In en, this message translates to:
  /// **'Name'**
  String get bundlesNameLabel;

  /// No description provided for @bundlesDomains.
  ///
  /// In en, this message translates to:
  /// **'Domains'**
  String get bundlesDomains;

  /// No description provided for @bundlesApps.
  ///
  /// In en, this message translates to:
  /// **'Apps'**
  String get bundlesApps;

  /// No description provided for @bundlesExportTooltip.
  ///
  /// In en, this message translates to:
  /// **'Download this bundle as a JSON file.'**
  String get bundlesExportTooltip;

  /// No description provided for @bundlesExport.
  ///
  /// In en, this message translates to:
  /// **'Export'**
  String get bundlesExport;

  /// No description provided for @bundlesEditsFooter.
  ///
  /// In en, this message translates to:
  /// **'Edits take effect at the next session start.'**
  String get bundlesEditsFooter;

  /// No description provided for @bundlesDeleteTooltip.
  ///
  /// In en, this message translates to:
  /// **'Permanently delete — this bundle has never been used in a session.'**
  String get bundlesDeleteTooltip;

  /// No description provided for @bundlesArchiveTooltip.
  ///
  /// In en, this message translates to:
  /// **'Hide from the picker. Hard delete is not possible because this bundle has been used in past sessions.'**
  String get bundlesArchiveTooltip;

  /// No description provided for @bundlesTest.
  ///
  /// In en, this message translates to:
  /// **'Test'**
  String get bundlesTest;

  /// No description provided for @bundlesTestHint.
  ///
  /// In en, this message translates to:
  /// **'Paste a URL or process name to see whether the current draft matches.'**
  String get bundlesTestHint;

  /// No description provided for @bundlesTestFieldHint.
  ///
  /// In en, this message translates to:
  /// **'e.g. https://www.geogebra.org/calc'**
  String get bundlesTestFieldHint;

  /// No description provided for @bundlesCheck.
  ///
  /// In en, this message translates to:
  /// **'Check'**
  String get bundlesCheck;

  /// No description provided for @bundlesValSignedPublisherDomain.
  ///
  /// In en, this message translates to:
  /// **'SignedPublisher is not valid for a domain.'**
  String get bundlesValSignedPublisherDomain;

  /// No description provided for @bundlesValInvalidDomain.
  ///
  /// In en, this message translates to:
  /// **'\"{value}\" is not a valid domain.'**
  String bundlesValInvalidDomain(String value);

  /// No description provided for @bundlesValMatchTypeApp.
  ///
  /// In en, this message translates to:
  /// **'{matchType} is not valid for an app.'**
  String bundlesValMatchTypeApp(String matchType);

  /// No description provided for @bundlesValProcessPath.
  ///
  /// In en, this message translates to:
  /// **'Process name \"{value}\" must not include a path.'**
  String bundlesValProcessPath(String value);

  /// No description provided for @bundlesValProcessExe.
  ///
  /// In en, this message translates to:
  /// **'Process name \"{value}\" must not include the .exe suffix.'**
  String bundlesValProcessExe(String value);

  /// No description provided for @bundlesKindDomain.
  ///
  /// In en, this message translates to:
  /// **'Domain'**
  String get bundlesKindDomain;

  /// No description provided for @bundlesKindApp.
  ///
  /// In en, this message translates to:
  /// **'App'**
  String get bundlesKindApp;

  /// No description provided for @bundlesMatchExact.
  ///
  /// In en, this message translates to:
  /// **'Exact'**
  String get bundlesMatchExact;

  /// No description provided for @bundlesMatchWildcard.
  ///
  /// In en, this message translates to:
  /// **'Wildcard'**
  String get bundlesMatchWildcard;

  /// No description provided for @bundlesMatchSuffix.
  ///
  /// In en, this message translates to:
  /// **'Suffix'**
  String get bundlesMatchSuffix;

  /// No description provided for @bundlesMatchSignedPublisher.
  ///
  /// In en, this message translates to:
  /// **'SignedPublisher'**
  String get bundlesMatchSignedPublisher;

  /// No description provided for @bundlesNoDomainEntries.
  ///
  /// In en, this message translates to:
  /// **'No domains entries.'**
  String get bundlesNoDomainEntries;

  /// No description provided for @bundlesNoAppEntries.
  ///
  /// In en, this message translates to:
  /// **'No apps entries.'**
  String get bundlesNoAppEntries;

  /// No description provided for @bundlesDomainHint.
  ///
  /// In en, this message translates to:
  /// **'e.g. *.geogebra.org'**
  String get bundlesDomainHint;

  /// No description provided for @bundlesAppHint.
  ///
  /// In en, this message translates to:
  /// **'e.g. msedge'**
  String get bundlesAppHint;

  /// No description provided for @bundlesRemoveEntry.
  ///
  /// In en, this message translates to:
  /// **'Remove entry'**
  String get bundlesRemoveEntry;

  /// No description provided for @addStudentSearchUnavailable.
  ///
  /// In en, this message translates to:
  /// **'Directory search unavailable.'**
  String get addStudentSearchUnavailable;

  /// No description provided for @addStudentLabel.
  ///
  /// In en, this message translates to:
  /// **'Add student'**
  String get addStudentLabel;

  /// No description provided for @addStudentSearchDisabled.
  ///
  /// In en, this message translates to:
  /// **'Search disabled'**
  String get addStudentSearchDisabled;

  /// No description provided for @addStudentSearchByName.
  ///
  /// In en, this message translates to:
  /// **'Search by name'**
  String get addStudentSearchByName;

  /// No description provided for @addStudentNoMatches.
  ///
  /// In en, this message translates to:
  /// **'No matches.'**
  String get addStudentNoMatches;

  /// No description provided for @adminsLoadError.
  ///
  /// In en, this message translates to:
  /// **'Could not load admins: {error}'**
  String adminsLoadError(String error);

  /// No description provided for @adminsSearchError.
  ///
  /// In en, this message translates to:
  /// **'Search failed: {error}'**
  String adminsSearchError(String error);

  /// No description provided for @adminsPromoteError.
  ///
  /// In en, this message translates to:
  /// **'Could not promote {name}: {error}'**
  String adminsPromoteError(String name, String error);

  /// No description provided for @adminsRemoveTitle.
  ///
  /// In en, this message translates to:
  /// **'Remove admin?'**
  String get adminsRemoveTitle;

  /// No description provided for @adminsRemoveBody.
  ///
  /// In en, this message translates to:
  /// **'\"{name}\" will lose admin access and return to a regular teacher/student role. They keep their account; you can promote them again later.'**
  String adminsRemoveBody(String name);

  /// No description provided for @adminsLastAdminError.
  ///
  /// In en, this message translates to:
  /// **'Can’t remove the last admin. Promote another user first.'**
  String get adminsLastAdminError;

  /// No description provided for @adminsRemoveError.
  ///
  /// In en, this message translates to:
  /// **'Could not remove {name}: {error}'**
  String adminsRemoveError(String name, String error);

  /// No description provided for @adminsCurrentAdmins.
  ///
  /// In en, this message translates to:
  /// **'Current admins'**
  String get adminsCurrentAdmins;

  /// No description provided for @adminsAddAdmin.
  ///
  /// In en, this message translates to:
  /// **'Add admin'**
  String get adminsAddAdmin;

  /// No description provided for @adminsAddHint.
  ///
  /// In en, this message translates to:
  /// **'Search a user who has signed in to the dashboard, then promote them.'**
  String get adminsAddHint;

  /// No description provided for @adminsSearchByName.
  ///
  /// In en, this message translates to:
  /// **'Search by name'**
  String get adminsSearchByName;

  /// No description provided for @adminsSearching.
  ///
  /// In en, this message translates to:
  /// **'Searching…'**
  String get adminsSearching;

  /// No description provided for @adminsNoCandidates.
  ///
  /// In en, this message translates to:
  /// **'No signed-in user matches. They must sign in to the dashboard at least once before they can be promoted.'**
  String get adminsNoCandidates;

  /// No description provided for @adminsNoAdmins.
  ///
  /// In en, this message translates to:
  /// **'No admins.'**
  String get adminsNoAdmins;

  /// No description provided for @schoolsLoadError.
  ///
  /// In en, this message translates to:
  /// **'Could not load schools: {error}'**
  String schoolsLoadError(String error);

  /// No description provided for @schoolsUpdateError.
  ///
  /// In en, this message translates to:
  /// **'Could not update {name}: {error}'**
  String schoolsUpdateError(String name, String error);

  /// No description provided for @schoolsHeader.
  ///
  /// In en, this message translates to:
  /// **'Schools'**
  String get schoolsHeader;

  /// No description provided for @schoolsIntro.
  ///
  /// In en, this message translates to:
  /// **'Active schools appear in the Classes school selector for teachers. Deactivate the ones your teachers don’t need.'**
  String get schoolsIntro;

  /// No description provided for @schoolsEmpty.
  ///
  /// In en, this message translates to:
  /// **'No schools found. Schools come from your directory; once teachers belong to one, it will appear here.'**
  String get schoolsEmpty;

  /// No description provided for @historyLoadError.
  ///
  /// In en, this message translates to:
  /// **'Could not load past sessions. Please try again.'**
  String get historyLoadError;

  /// No description provided for @historyEmpty.
  ///
  /// In en, this message translates to:
  /// **'No past sessions yet. Sessions appear here after you end them.'**
  String get historyEmpty;

  /// No description provided for @historyPastSessions.
  ///
  /// In en, this message translates to:
  /// **'Past sessions'**
  String get historyPastSessions;

  /// No description provided for @historySessionFallback.
  ///
  /// In en, this message translates to:
  /// **'Session'**
  String get historySessionFallback;

  /// No description provided for @pastLoadError.
  ///
  /// In en, this message translates to:
  /// **'Could not load past session: {error}'**
  String pastLoadError(String error);

  /// No description provided for @pastNotAvailable.
  ///
  /// In en, this message translates to:
  /// **'Session not available.'**
  String get pastNotAvailable;

  /// No description provided for @pastSessionFallback.
  ///
  /// In en, this message translates to:
  /// **'Session'**
  String get pastSessionFallback;

  /// No description provided for @pastBundlesUsed.
  ///
  /// In en, this message translates to:
  /// **'Bundles used'**
  String get pastBundlesUsed;

  /// No description provided for @pastParticipants.
  ///
  /// In en, this message translates to:
  /// **'Participants'**
  String get pastParticipants;

  /// No description provided for @pastActivitySummary.
  ///
  /// In en, this message translates to:
  /// **'Activity summary'**
  String get pastActivitySummary;

  /// No description provided for @pastApprovedExceptions.
  ///
  /// In en, this message translates to:
  /// **'Approved exceptions'**
  String get pastApprovedExceptions;

  /// No description provided for @pastUnapprovedRequests.
  ///
  /// In en, this message translates to:
  /// **'Unapproved requests'**
  String get pastUnapprovedRequests;

  /// No description provided for @pastEventLog.
  ///
  /// In en, this message translates to:
  /// **'Event log'**
  String get pastEventLog;

  /// No description provided for @pastStatusDeclinedAt.
  ///
  /// In en, this message translates to:
  /// **'declined at {time}'**
  String pastStatusDeclinedAt(String time);

  /// No description provided for @pastStatusNeverJoined.
  ///
  /// In en, this message translates to:
  /// **'never joined'**
  String get pastStatusNeverJoined;

  /// No description provided for @pastStatusJoinedAt.
  ///
  /// In en, this message translates to:
  /// **'joined {time}'**
  String pastStatusJoinedAt(String time);

  /// No description provided for @pastStatusJoinedLeft.
  ///
  /// In en, this message translates to:
  /// **'joined {joined}  ·  left {left}'**
  String pastStatusJoinedLeft(String joined, String left);

  /// No description provided for @pastNoEventDetail.
  ///
  /// In en, this message translates to:
  /// **'No event detail retained for this session.'**
  String get pastNoEventDetail;
}

class _AppLocalizationsDelegate
    extends LocalizationsDelegate<AppLocalizations> {
  const _AppLocalizationsDelegate();

  @override
  Future<AppLocalizations> load(Locale locale) {
    return SynchronousFuture<AppLocalizations>(lookupAppLocalizations(locale));
  }

  @override
  bool isSupported(Locale locale) =>
      <String>['en', 'nl'].contains(locale.languageCode);

  @override
  bool shouldReload(_AppLocalizationsDelegate old) => false;
}

AppLocalizations lookupAppLocalizations(Locale locale) {
  // Lookup logic when only language code is specified.
  switch (locale.languageCode) {
    case 'en':
      return AppLocalizationsEn();
    case 'nl':
      return AppLocalizationsNl();
  }

  throw FlutterError(
    'AppLocalizations.delegate failed to load unsupported locale "$locale". This is likely '
    'an issue with the localizations generation tool. Please file an issue '
    'on GitHub with a reproducible sample app and the gen-l10n configuration '
    'that was used.',
  );
}
