// Canonical Italian dictionary for the mobile app (source of truth for keys).
// English (en.ts) is typed against these keys and falls back to Italian for any
// missing key, so a raw key is never rendered. The app localizes in the signed-in
// user's persisted language (from /api/auth/me); Italian is the default.
const it = {
  'common.loading': 'Caricamento…',
  'common.retry': 'Riprova',
  'common.signOut': 'Esci',
  'common.signOutConfirmBody': 'Vuoi uscire dal tuo account?',
  'common.back': '‹ Indietro',

  'app.restoring': 'Ripristino della sessione…',

  'login.sessionExpired': 'La tua sessione è scaduta. Accedi di nuovo.',
  'login.apiBaseUrl': 'URL base API',
  'login.email': 'Email',
  'login.password': 'Password',
  'login.signIn': 'Accedi',
  'login.errorCredentials': 'Email o password non valide.',
  'login.errorNetwork': 'Impossibile raggiungere il server. Controlla URL e connessione.',

  'gallery.home': 'Home',
  'gallery.photos': 'Foto',
  'gallery.files': 'File',
  'gallery.loadMore': 'Carica altri',
  'gallery.loadMoreError': 'Impossibile caricare altri — tocca per riprovare',
  'gallery.noPhotos': 'Ancora nessuna foto.',
  'gallery.folderEmpty': 'Questa cartella è vuota.',
  'gallery.pullToRefresh': 'Trascina verso il basso per aggiornare.',
  'gallery.whatPhotos': 'le foto',
  'gallery.whatVideos': 'i video',
  'gallery.whatFolder': 'questa cartella',
  'gallery.loadErrorHttp': 'Impossibile caricare {what} (errore {status}).',
  'gallery.loadErrorNetwork': 'Impossibile caricare {what}. Controlla la connessione.',

  // Tabs
  'tabs.photos': 'Foto',
  'tabs.videos': 'Video',
  'tabs.albums': 'Album',
  'tabs.files': 'File',

  // Photos / Videos grids
  'grid.emptyPhotos': 'Nessuna foto.',
  'grid.emptyVideos': 'Nessun video.',
  'grid.emptyHint': 'Carica i media dal web per vederli qui.',
  'grid.errorTitle': 'Impossibile caricare la libreria',
  'grid.duplicateBadge': '{count}×',
  'grid.videoNoPoster': 'Video senza anteprima',
  'grid.syntheticPoster': 'Anteprima non disponibile',

  // Viewer
  'viewer.back': 'Torna indietro',
  'viewer.toggleChrome': 'Mostra o nascondi i controlli',

  // Video player
  'player.retry': 'Riprova la riproduzione',
  'player.playbackError': 'Riproduzione impossibile.',
  'player.loading': 'Preparazione del video…',
  'player.preparing': 'Il video si sta preparando. Riproviamo fra poco…',
  'player.back': 'Chiudi il video',

  // Albums
  'albums.empty': 'Nessun album.',
  'albums.emptyHint': 'Crea il tuo primo album.',
  'albums.create': 'Nuovo album',
  'albums.createTitle': 'Nuovo album',
  'albums.nameLabel': 'Nome',
  'albums.descriptionLabel': 'Descrizione (opzionale)',
  'albums.save': 'Salva',
  'albums.saveError': 'Salvataggio non riuscito. Riprova.',
  'albums.cancel': 'Annulla',
  'albums.rename': 'Rinomina',
  'albums.edit': 'Modifica',
  'albums.delete': 'Elimina album',
  'albums.deleteConfirmTitle': 'Eliminare l\'album?',
  'albums.deleteConfirmBody':
    'L\'album "{name}" verrà eliminato. Le foto e i video al suo interno NON vengono cancellati dalla libreria.',
  'albums.photoCount': '{count} foto',
  'albums.videoCount': '{count} video',
  'albums.itemCounts': '{photos}, {videos}',
  'albums.open': 'Apri l\'album {name}',
  'albumDetail.empty': 'Album vuoto.',
  'albumDetail.emptyHint': 'Aggiungi media dalla libreria.',
  'albumDetail.addMedia': 'Aggiungi media',
  'albumDetail.removeSelected': 'Rimuovi dall\'album ({count})',
  'albumDetail.removedNotice': '{count} elementi rimossi dall\'album. I file restano nella tua libreria.',
  'albumDetail.select': 'Seleziona',
  'albumDetail.cancelSelection': 'Annulla selezione',
  'albumDetail.deletedMedia': 'Elemento non più disponibile',

  // Selection / add-to-album
  'selection.addToAlbum': 'Aggiungi all\'album',
  'selection.select': 'Seleziona',
  'selection.selectedCount': '{count} selezionati',
  'selection.chooseAlbum': 'Scegli un album',
  'selection.createNew': 'Crea nuovo album…',
  'selection.addedNotice': '{succeeded} aggiunti, {skipped} già presenti o ignorati.',
  'selection.addedAll': '{count} elementi aggiunti all\'album.',

  // Unified albums / shared
  'albums.filterAll': 'Tutti',
  'albums.filterMine': 'Miei',
  'albums.filterShared': 'Condivisi',
  'shared.pendingInvitations': 'Inviti in attesa',
  'shared.inviteAccept': 'Accetta',
  'shared.inviteDecline': 'Rifiuta',
  'shared.inviteAction': "la risposta all'invito",
  'shared.badgeShared': 'Condiviso da',
  'shared.sharedBy': 'di {name}',
  'shared.roleViewer': 'Visualizzatore',
  'shared.roleContributor': 'Contributore',
  'shared.roleEditor': 'Editor',
  'shared.itemsCount': '{count} elementi',
  'shared.contribute': 'Aggiungi media',
  'shared.itemActions': 'Azioni elemento',
  'shared.withdrawTitle': 'Rimuovere il tuo contributo?',
  'shared.withdrawConfirmBody': 'Il file NON viene cancellato: resta nella tua libreria.',
  'shared.withdrawAction': 'Rimuovi contributo',
  'shared.withdrawDone': 'Contributo rimosso dall album.',
  'shared.withdrawFailed': 'Rimozione non riuscita. Riprova.',
  'shared.download': 'Scarica',
  'shared.downloadFailed': 'Download non riuscito.',

  // Files
  'files.breadcrumbHome': 'Home',

  // Login extras
  'login.serverHint': 'Es. http://10.0.0.5:5177 in rete locale',
} as const;

export type MobileMessageKey = keyof typeof it;

export default it;
