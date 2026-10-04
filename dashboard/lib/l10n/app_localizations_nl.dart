// ignore: unused_import
import 'package:intl/intl.dart' as intl;
import 'app_localizations.dart';

// ignore_for_file: type=lint

/// The translations for Dutch Flemish (`nl`).
class AppLocalizationsNl extends AppLocalizations {
  AppLocalizationsNl([String locale = 'nl']) : super(locale);

  @override
  String get actionCancel => 'Annuleren';

  @override
  String get actionDelete => 'Verwijderen';

  @override
  String get actionRemove => 'Verwijderen';

  @override
  String get actionAdd => 'Toevoegen';

  @override
  String get actionSave => 'Opslaan';

  @override
  String get actionCreate => 'Aanmaken';

  @override
  String get actionImport => 'Importeren';

  @override
  String get actionClose => 'Sluiten';

  @override
  String get actionRetry => 'Opnieuw';

  @override
  String get actionLoadMore => 'Meer laden';

  @override
  String get actionApprove => 'Goedkeuren';

  @override
  String get badgeLive => 'Live';

  @override
  String get badgeEnded => 'Beëindigd';

  @override
  String get badgeArchived => 'Gearchiveerd';

  @override
  String get statusActive => 'Actief';

  @override
  String get statusInactive => 'Inactief';

  @override
  String get commonLoading => 'Laden…';

  @override
  String get commonNone => '(geen)';

  @override
  String get loginEyebrow => 'Anchor voor leerkrachten';

  @override
  String get loginHeadline => 'Klaar wanneer je klas dat is.';

  @override
  String get loginSubtitle =>
      'Meld je aan met je schoolaccount om een focussessie voor een klas te starten.';

  @override
  String get loginSignInButton => 'Aanmelden met Microsoft';

  @override
  String get loginTimeoutError =>
      'Aanmelden duurt langer dan verwacht. Probeer het opnieuw.';

  @override
  String get loginError => 'Kon je niet aanmelden. Probeer het opnieuw.';

  @override
  String get shellNavHome => 'Start';

  @override
  String get shellNavClasses => 'Klassen';

  @override
  String get shellNavHistory => 'Geschiedenis';

  @override
  String get shellNavAdmin => 'Beheer';

  @override
  String get shellSignOut => 'AFMELDEN';

  @override
  String get shellSectionPastSessions => 'Afgelopen sessies';

  @override
  String get shellSectionLiveSession => 'Live sessie';

  @override
  String get shellSectionPastSession => 'Afgelopen sessie';

  @override
  String get adminNavBundles => 'Bundels';

  @override
  String get adminNavAdmins => 'Beheerders';

  @override
  String get adminNavSchools => 'Scholen';

  @override
  String get apiError403 =>
      'Je account is nog niet ingesteld als leerkracht. Vraag een beheerder om toegang te verlenen.';

  @override
  String get apiError403Admin =>
      'Je account heeft geen beheerderstoegang. Vraag een beheerder om die te verlenen.';

  @override
  String get homeLoadError =>
      'Kon de sessiegegevens niet laden. Probeer het opnieuw.';

  @override
  String get homeStartError =>
      'Kon de sessie niet starten. Probeer het opnieuw.';

  @override
  String get homeEndError =>
      'Kon de sessie niet beëindigen. Probeer het opnieuw.';

  @override
  String get homeActiveSessionFallback => 'Actieve sessie';

  @override
  String homeStillRunning(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: 'Nog actief — $count sessies',
      one: 'Nog actief',
    );
    return '$_temp0';
  }

  @override
  String get homeHeadline => 'Start een focussessie.';

  @override
  String get homeSubtitle =>
      'Kies een klas en start. Leerlingen sluiten aan met de code op het sessiescherm.';

  @override
  String get homeNoClasses => 'Nog geen klassen aan jou toegewezen.';

  @override
  String homeYourDepartment(String department) {
    return 'JOUW AFDELING · $department';
  }

  @override
  String get homeClassLabel => 'Klas';

  @override
  String get homeSelectClass => 'Selecteer een klas';

  @override
  String homeStartSessionFor(String name) {
    return 'Start sessie voor $name';
  }

  @override
  String homeStartedAt(String time) {
    return 'GESTART $time';
  }

  @override
  String get homeResume => 'Hervatten';

  @override
  String get homeEnd => 'Beëindigen';

  @override
  String get classesLoadError =>
      'Kon de klassen niet laden. Probeer het opnieuw.';

  @override
  String get classesLoadSchoolsError =>
      'Kon de scholen niet laden. De keuzelijst is mogelijk onvolledig.';

  @override
  String get classesLoadRosterError =>
      'Kon de klaslijst niet laden. Probeer het opnieuw.';

  @override
  String get classesDeleteClass => 'Klas verwijderen';

  @override
  String classesDeleteClassBody(String name, String year) {
    return '$name ($year) verwijderen? Dit verwijdert de klas en haar klaslijst.';
  }

  @override
  String get classesDeleteError =>
      'Kon de klas niet verwijderen. Probeer het opnieuw.';

  @override
  String classesDeleteWithSessionsBody(int count, String name, String year) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other:
          '$name ($year) heeft $count sessies. Als je de klas verwijdert, verdwijnen die sessies ook, en daarmee de activiteitsgegevens van de leerlingen. Dit kan niet ongedaan worden gemaakt.',
      one:
          '$name ($year) heeft 1 sessie. Als je de klas verwijdert, verdwijnt die sessie ook, en daarmee de activiteitsgegevens van de leerlingen. Dit kan niet ongedaan worden gemaakt.',
    );
    return '$_temp0';
  }

  @override
  String get classesDeleteArchiveInstead =>
      'Wil je de klas alleen uit je lijsten halen? Archiveer ze dan: de sessies blijven bewaard en je kunt de klas later herstellen.';

  @override
  String classesDeleteTypeName(String name) {
    return 'Typ $name om te bevestigen';
  }

  @override
  String get classesDeleteWithSessions => 'Klas en sessies verwijderen';

  @override
  String get classesDeleteRunningError =>
      'Er loopt nog een sessie van deze klas. Beëindig ze en verwijder dan de klas.';

  @override
  String get classesDeleteHasSessionsError =>
      'Intussen is er een sessie voor deze klas gestart. Laad de pagina opnieuw en probeer het nog eens.';

  @override
  String get classesArchive => 'Archiveren';

  @override
  String get classesArchiveTitle => 'Klas archiveren?';

  @override
  String classesArchiveBody(String name, String year) {
    return '$name ($year) verdwijnt uit je klassenlijst en uit de klaskeuze op het startscherm, en er kan geen sessie meer voor starten. De klaslijst en afgelopen sessies blijven bewaard. Je kunt de klas herstellen onder Gearchiveerd, boven de klassenlijst.';
  }

  @override
  String get classesArchiveError =>
      'Kon de klas niet archiveren. Probeer het opnieuw.';

  @override
  String get classesRestore => 'Herstellen';

  @override
  String get classesRestoreError =>
      'Kon de klas niet herstellen. Probeer het opnieuw.';

  @override
  String get classesShowArchived => 'Gearchiveerde klassen tonen';

  @override
  String get classesSaveCodesError =>
      'Kon school + code niet opslaan. Probeer het opnieuw.';

  @override
  String get classesAddMemberError =>
      'Kon het lid niet toevoegen. Probeer het opnieuw.';

  @override
  String get classesRemoveMemberTitle => 'Lid verwijderen';

  @override
  String classesRemoveMemberBody(String name, String className) {
    return '$name uit $className verwijderen? Vanaf de volgende sessiestart ontvangen zij geen sessie-uitzendingen meer.';
  }

  @override
  String get classesRemoveMemberError =>
      'Kon het lid niet verwijderen. Probeer het opnieuw.';

  @override
  String get classesCsvNoRows => 'Geen geldige rijen in de CSV.';

  @override
  String get classesImportError => 'Importeren mislukt. Probeer het opnieuw.';

  @override
  String get classesGraphImportError =>
      'Vullen vanuit Graph mislukt. Probeer het opnieuw.';

  @override
  String get classesPickClass => 'Kies links een klas.';

  @override
  String get classesListHeader => 'Klassen';

  @override
  String get classesNewClass => 'Nieuwe klas';

  @override
  String get classesEmpty =>
      'Je geeft nog geen lessen aan een klas. Maak er een met \"Nieuwe klas\".';

  @override
  String get classesSetScopeFirst => 'Stel eerst school + code in';

  @override
  String get classesImportCsv => 'CSV importeren';

  @override
  String get classesPopulateFromGraph => 'Vullen vanuit Graph';

  @override
  String get classesSchoolLabel => 'School';

  @override
  String get classesClassCodeLabel => 'Klascode';

  @override
  String get classesHint3A => 'bijv. 3A';

  @override
  String get classesNoMembers => 'Nog geen leden.';

  @override
  String get classesDisplayName => 'Weergavenaam';

  @override
  String get classesRole => 'Rol';

  @override
  String classesImportAdded(int count) {
    return '$count toegevoegd';
  }

  @override
  String classesImportAlready(int count) {
    return '$count al lid';
  }

  @override
  String classesImportUnresolved(int count) {
    return '$count niet gevonden';
  }

  @override
  String classesImportWrongSchool(int count) {
    return '$count verkeerde school';
  }

  @override
  String get classesBlank => '(leeg)';

  @override
  String get classesCreateError =>
      'Kon de klas niet aanmaken. Probeer het opnieuw.';

  @override
  String classesCreateArchivedClash(String name, String year) {
    return '$name ($year) bestaat al en is gearchiveerd. Herstel die klas: zet Gearchiveerd aan boven de klassenlijst.';
  }

  @override
  String get classesNameLabel => 'Naam';

  @override
  String get classesSchoolYearLabel => 'Schooljaar';

  @override
  String get classesSchoolYearHint => 'bijv. 2025-2026';

  @override
  String get classesSchoolOptionalLabel => 'School (optioneel)';

  @override
  String get classesClassCodeOptionalLabel => 'Klascode (optioneel)';

  @override
  String get classesCsvDialogTitle => 'Klaslijst importeren uit CSV';

  @override
  String get classesCsvDialogBody =>
      'Plak een CSV met een koprij. Vereiste kolom: upn (de user principal name, bijv. leerling@school.be). Namen worden automatisch in de directory opgezocht.';

  @override
  String get classesCsvHint => 'upn\nan@school.be\njan@school.be\n...';

  @override
  String get classesCsvEmpty => 'De CSV is leeg.';

  @override
  String get classesCsvHeaderMissing => 'De koprij moet upn bevatten.';

  @override
  String get sessionUpdateBundlesError =>
      'Kon de toegestane bundels niet bijwerken. Probeer het opnieuw.';

  @override
  String get sessionApproveError =>
      'Kon het verzoek niet goedkeuren. Probeer het opnieuw.';

  @override
  String sessionCopiedCode(String code) {
    return '$code gekopieerd';
  }

  @override
  String get sessionConnectError =>
      'Kon geen verbinding maken met de live feed van deze sessie. Laad de pagina opnieuw om het nog eens te proberen.';

  @override
  String get sessionEndError =>
      'Kon de sessie niet beëindigen. Probeer het opnieuw.';

  @override
  String get sessionLeaveTitle => 'Deze sessie verlaten?';

  @override
  String get sessionLeaveBody =>
      'Deze sessie loopt nog en leerlingen blijven afgeschermd. Beëindig ze voor iedereen, of laat ze lopen en kom er later op terug via het startscherm?';

  @override
  String get sessionLeaveRunning => 'Laten lopen';

  @override
  String get sessionEndSession => 'Sessie beëindigen';

  @override
  String get sessionTitleFallback => 'Sessie';

  @override
  String sessionTitleSpec(String datetime) {
    return 'Sessie $datetime';
  }

  @override
  String get sessionHeadlineFallback => 'Live sessie';

  @override
  String get sessionConnecting => 'Verbinden met de live feed…';

  @override
  String get sessionReconnecting =>
      'Verbinding verbroken — opnieuw verbinden. Live-updates zijn gepauzeerd.';

  @override
  String get sessionDisconnected =>
      'Geen verbinding — live-updates zijn gepauzeerd.';

  @override
  String get sessionReconnect => 'Opnieuw verbinden';

  @override
  String get sessionActivity => 'Activiteit';

  @override
  String get sessionLeaveTooltip => 'Sessie verlaten';

  @override
  String get sessionEndedBanner =>
      'Sessie beëindigd — de gebeurtenissenstroom is gestopt.';

  @override
  String get sessionWaitingEvents => 'Wachten op gebeurtenissen…';

  @override
  String get sessionStateStale => 'Agent rapporteert niet meer';

  @override
  String get sessionStateLeft => 'Verlaten';

  @override
  String get sessionStateDeclined => 'Geweigerd';

  @override
  String get sessionStateNotJoined => 'Niet aangesloten';

  @override
  String get sessionStateInSession => 'In sessie';

  @override
  String get sessionStateUnknown => 'Onbekend';

  @override
  String sessionStudentsCount(int joined, int total) {
    return 'Leerlingen ($joined/$total in sessie)';
  }

  @override
  String get sessionTamperTooltip => 'Manipulatie gedetecteerd';

  @override
  String get sessionPendingRequests => 'Openstaande verzoeken';

  @override
  String sessionRequesters(int count, String names) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count leerlingen — $names',
      one: '1 leerling — $names',
    );
    return '$_temp0';
  }

  @override
  String get sessionMoreApprovalOptions => 'Meer goedkeuringsopties';

  @override
  String get sessionNotYours =>
      'Deze sessie is niet beschikbaar voor jou. Alleen de leerkracht die ze gestart heeft, kan ze openen.';

  @override
  String get sessionApproveWholeClass => 'Goedkeuren voor hele klas';

  @override
  String get sessionSummary => 'Sessieoverzicht';

  @override
  String get sessionAllowedBundles => 'Toegestane bundels';

  @override
  String get sessionNoBundles => 'Nog geen bundels beschikbaar.';

  @override
  String get sessionJoinCode => 'Toegangscode';

  @override
  String get sessionCopyCode => 'Code kopiëren';

  @override
  String get bundlesLoadListError =>
      'Kon de bundels niet laden. Probeer het opnieuw.';

  @override
  String get bundlesLoadOneError =>
      'Kon deze bundel niet laden. Probeer het opnieuw.';

  @override
  String get bundlesNameRequired => 'Naam is verplicht.';

  @override
  String get bundlesEntryValueRequired => 'Elk item moet een waarde hebben.';

  @override
  String get bundlesEntryAtLeastOne => 'Er is minstens één item vereist.';

  @override
  String get bundlesSaveError =>
      'Kon de bundel niet opslaan. Probeer het opnieuw.';

  @override
  String get bundlesNameTaken =>
      'Er bestaat al een bundel met die naam. Kies een andere naam.';

  @override
  String get bundlesArchiveTitle => 'Bundel archiveren?';

  @override
  String bundlesArchiveBody(String name) {
    return '\"$name\" wordt verborgen in de keuzelijst. Afgelopen sessies die ernaar verwijzen blijven intact. Je kunt de bundel later herstellen door te bewerken.';
  }

  @override
  String get bundlesArchive => 'Archiveren';

  @override
  String get bundlesArchiveError =>
      'Kon de bundel niet archiveren. Probeer het opnieuw.';

  @override
  String get bundlesDeleteTitle => 'Bundel verwijderen?';

  @override
  String bundlesDeleteBody(String name) {
    return '\"$name\" wordt permanent verwijderd. Dit kan niet ongedaan worden gemaakt. Beschikbaar omdat geen enkele sessie deze bundel ooit heeft gebruikt.';
  }

  @override
  String get bundlesDeleteError =>
      'Kon de bundel niet verwijderen. Probeer het opnieuw.';

  @override
  String get bundlesDeleteUsedError =>
      'Een sessie heeft deze bundel gebruikt, dus je kunt hem niet verwijderen. Archiveer hem in de plaats.';

  @override
  String bundlesSaveFailedFor(String name) {
    return 'Kon \"$name\" niet opslaan.';
  }

  @override
  String bundlesArchiveFailedFor(String name) {
    return 'Kon \"$name\" niet archiveren.';
  }

  @override
  String bundlesDeleteFailedFor(String name) {
    return 'Kon \"$name\" niet verwijderen.';
  }

  @override
  String get bundlesTryAgain => 'Probeer het opnieuw.';

  @override
  String bundlesNameTakenBy(String name) {
    return 'Er bestaat al een andere bundel met de naam \"$name\".';
  }

  @override
  String get bundlesUsedArchiveInstead =>
      'Een sessie heeft hem gebruikt. Archiveer hem in de plaats.';

  @override
  String get bundlesReopen => 'Opnieuw openen';

  @override
  String bundlesTestNoMatch(String probe) {
    return 'Geen item komt overeen met \"$probe\".';
  }

  @override
  String bundlesTestMatch(String value, String kind, String matchType) {
    return 'Komt overeen met \"$value\" ($kind / $matchType).';
  }

  @override
  String get bundlesNothingToExport =>
      'Niets om te exporteren — de catalogus is leeg.';

  @override
  String bundlesExported(int count) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count bundels',
      one: '1 bundel',
    );
    return '$_temp0 geëxporteerd.';
  }

  @override
  String get bundlesExportError =>
      'Kon de bundels niet exporteren. Probeer het opnieuw.';

  @override
  String bundlesImported(int count, int created, int updated) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count bundels',
      one: '1 bundel',
    );
    return '$_temp0 geïmporteerd ($created aangemaakt, $updated bijgewerkt).';
  }

  @override
  String bundlesImportedWithFailures(int count, int created, int updated) {
    String _temp0 = intl.Intl.pluralLogic(
      count,
      locale: localeName,
      other: '$count fouten',
      one: '1 fout',
    );
    return 'Geïmporteerd met $_temp0 ($created aangemaakt, $updated bijgewerkt)';
  }

  @override
  String get bundlesImportError =>
      'Kon de bundels niet importeren. Probeer het opnieuw.';

  @override
  String bundlesImportOneError(String name) {
    return '\"$name\" kon niet opgeslagen worden.';
  }

  @override
  String get bundlesImportRejected => 'Import geweigerd';

  @override
  String get bundlesImportDetails => 'Details';

  @override
  String get bundlesAdminRequired => 'Beheerderstoegang vereist.';

  @override
  String get bundlesCatalogue => 'Catalogus';

  @override
  String get bundlesShowArchived => 'Gearchiveerde bundels tonen';

  @override
  String get bundlesNewBundle => 'Nieuwe bundel';

  @override
  String get bundlesExportAll => 'Alles exporteren';

  @override
  String get bundlesNoBundles => 'Geen bundels.';

  @override
  String get bundlesSelectOrNew => 'Selecteer een bundel of begin een nieuwe.';

  @override
  String get bundlesNameLabel => 'Naam';

  @override
  String get bundlesDomains => 'Domeinen';

  @override
  String get bundlesApps => 'Apps';

  @override
  String get bundlesExportTooltip => 'Download deze bundel als JSON-bestand.';

  @override
  String get bundlesExport => 'Exporteren';

  @override
  String get bundlesEditsFooter =>
      'Wijzigingen worden van kracht bij de volgende sessiestart.';

  @override
  String get bundlesDeleteTooltip =>
      'Permanent verwijderen — deze bundel is nooit in een sessie gebruikt.';

  @override
  String get bundlesArchiveTooltip =>
      'Verbergen in de keuzelijst. Definitief verwijderen kan niet omdat deze bundel in afgelopen sessies is gebruikt.';

  @override
  String get bundlesTest => 'Testen';

  @override
  String get bundlesTestHint =>
      'Plak een URL of procesnaam om te zien of het huidige concept overeenkomt.';

  @override
  String get bundlesTestFieldHint => 'bijv. https://www.geogebra.org/calc';

  @override
  String get bundlesCheck => 'Controleren';

  @override
  String get bundlesValSignedPublisherDomain =>
      'SignedPublisher is niet geldig voor een domein.';

  @override
  String bundlesValInvalidDomain(String value) {
    return '\"$value\" is geen geldig domein.';
  }

  @override
  String bundlesValMatchTypeApp(String matchType) {
    return '$matchType is niet geldig voor een app.';
  }

  @override
  String bundlesValProcessPath(String value) {
    return 'Procesnaam \"$value\" mag geen pad bevatten.';
  }

  @override
  String bundlesValProcessExe(String value) {
    return 'Procesnaam \"$value\" mag niet eindigen op .exe.';
  }

  @override
  String get bundlesKindDomain => 'Domein';

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
  String get bundlesNoDomainEntries => 'Geen domeinitems.';

  @override
  String get bundlesNoAppEntries => 'Geen app-items.';

  @override
  String get bundlesDomainHint => 'bijv. *.geogebra.org';

  @override
  String get bundlesAppHint => 'bijv. msedge';

  @override
  String get bundlesRemoveEntry => 'Item verwijderen';

  @override
  String get addStudentSearchUnavailable =>
      'Directory-zoeken niet beschikbaar.';

  @override
  String get addStudentLabel => 'Leerling toevoegen';

  @override
  String get addStudentSearchDisabled => 'Zoeken uitgeschakeld';

  @override
  String get addStudentSearchByName => 'Zoek op naam';

  @override
  String get addStudentNoMatches => 'Geen resultaten.';

  @override
  String get adminsLoadError =>
      'Kon de beheerders niet laden. Probeer het opnieuw.';

  @override
  String get adminsSearchError => 'Zoeken mislukt. Probeer het opnieuw.';

  @override
  String adminsPromoteError(String name) {
    return 'Kon $name niet promoveren. Probeer het opnieuw.';
  }

  @override
  String get adminsRemoveTitle => 'Beheerder verwijderen?';

  @override
  String adminsRemoveBody(String name) {
    return '\"$name\" verliest beheerderstoegang en keert terug naar een gewone leerkracht-/leerlingrol. Het account blijft behouden; je kunt deze persoon later opnieuw promoveren.';
  }

  @override
  String get adminsLastAdminError =>
      'Kan de laatste beheerder niet verwijderen. Promoveer eerst een andere gebruiker.';

  @override
  String adminsRemoveError(String name) {
    return 'Kon $name niet verwijderen. Probeer het opnieuw.';
  }

  @override
  String get adminsCurrentAdmins => 'Huidige beheerders';

  @override
  String get adminsAddAdmin => 'Beheerder toevoegen';

  @override
  String get adminsAddHint =>
      'Zoek een gebruiker die zich op het dashboard heeft aangemeld en promoveer die.';

  @override
  String get adminsSearchByName => 'Zoek op naam';

  @override
  String get adminsSearching => 'Zoeken…';

  @override
  String get adminsNoCandidates =>
      'Geen aangemelde gebruiker komt overeen. Ze moeten zich minstens één keer op het dashboard aanmelden voordat ze gepromoveerd kunnen worden.';

  @override
  String get adminsNoAdmins => 'Geen beheerders.';

  @override
  String get schoolsLoadError =>
      'Kon de scholen niet laden. Probeer het opnieuw.';

  @override
  String schoolsUpdateError(String name) {
    return 'Kon $name niet bijwerken. Probeer het opnieuw.';
  }

  @override
  String get schoolsHeader => 'Scholen';

  @override
  String get schoolsIntro =>
      'Actieve scholen verschijnen in de schoolkeuzelijst voor leerkrachten bij Klassen. Deactiveer degene die je leerkrachten niet nodig hebben.';

  @override
  String get schoolsEmpty =>
      'Geen scholen gevonden. Scholen komen uit je directory; zodra leerkrachten bij een school horen, verschijnt die hier.';

  @override
  String get historyLoadError =>
      'Kon de afgelopen sessies niet laden. Probeer het opnieuw.';

  @override
  String get historyEmpty =>
      'Nog geen afgelopen sessies. Sessies verschijnen hier nadat je ze beëindigt.';

  @override
  String get historyPastSessions => 'Afgelopen sessies';

  @override
  String get historySessionFallback => 'Sessie';

  @override
  String get pastLoadError =>
      'Kon deze afgelopen sessie niet laden. Probeer het opnieuw.';

  @override
  String get pastNotAvailable => 'Sessie niet beschikbaar.';

  @override
  String get pastSessionFallback => 'Sessie';

  @override
  String get pastBundlesUsed => 'Gebruikte bundels';

  @override
  String get pastParticipants => 'Deelnemers';

  @override
  String get pastActivitySummary => 'Activiteitsoverzicht';

  @override
  String get pastApprovedExceptions => 'Goedgekeurde uitzonderingen';

  @override
  String get pastUnapprovedRequests => 'Niet-goedgekeurde verzoeken';

  @override
  String get pastEventLog => 'Gebeurtenissenlog';

  @override
  String pastStatusDeclinedAt(String time) {
    return 'geweigerd om $time';
  }

  @override
  String get pastStatusNeverJoined => 'nooit aangesloten';

  @override
  String pastStatusJoinedAt(String time) {
    return 'aangesloten $time';
  }

  @override
  String pastStatusJoinedLeft(String joined, String left) {
    return 'aangesloten $joined  ·  verlaten $left';
  }

  @override
  String get pastNoEventDetail =>
      'Geen gebeurtenisdetails bewaard voor deze sessie.';
}
