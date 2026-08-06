// Canonical Italian dictionary for the mobile app (source of truth for keys).
// English (en.ts) is typed against these keys and falls back to Italian for any
// missing key, so a raw key is never rendered. The app localizes in the signed-in
// user's persisted language (from /api/auth/me); Italian is the default.
const it = {
  'common.loading': 'Caricamento…',
  'common.retry': 'Riprova',
  'common.signOut': 'Esci',
  'common.back': '‹ Indietro',

  'app.restoring': 'Ripristino della sessione…',

  'login.sessionExpired': 'La tua sessione è scaduta. Accedi di nuovo.',
  'login.apiBaseUrl': 'URL base API',
  'login.email': 'Email',
  'login.password': 'Password',
  'login.signIn': 'Accedi',

  'gallery.home': 'Home',
  'gallery.photos': 'Foto',
  'gallery.files': 'File',
  'gallery.loadMore': 'Carica altri',
  'gallery.loadMoreError': 'Impossibile caricare altri — tocca per riprovare',
  'gallery.noPhotos': 'Ancora nessuna foto.',
  'gallery.folderEmpty': 'Questa cartella è vuota.',
  'gallery.pullToRefresh': 'Trascina verso il basso per aggiornare.',
  'gallery.whatPhotos': 'le foto',
  'gallery.whatFolder': 'questa cartella',
  'gallery.loadErrorHttp': 'Impossibile caricare {what} (errore {status}).',
  'gallery.loadErrorNetwork': 'Impossibile caricare {what}. Controlla la connessione.',
} as const;

export type MobileMessageKey = keyof typeof it;

export default it;
