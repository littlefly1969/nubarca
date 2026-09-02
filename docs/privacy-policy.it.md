# NubArca — Informativa sulla privacy

**Ultimo aggiornamento: 2 settembre 2026**

## La cosa che spiega tutto il resto

NubArca è **software self-hosted**. Non è un servizio. Non esiste un cloud
NubArca, non esiste un account NubArca e non esiste alcun server gestito dallo
sviluppatore che le tue foto possano raggiungere.

L'app parla con **un solo server: quello il cui indirizzo digiti nella schermata
di accesso**. Quel server lo gestisci tu, o chi ti ha dato l'indirizzo. Le tue
foto, il tuo account e tutto ciò che ne deriva stanno lì e in nessun altro
posto.

Ne derivano due ruoli distinti, e la differenza conta giuridicamente:

| Ruolo | Chi | Cosa detiene |
| --- | --- | --- |
| **Sviluppatore** | l'autore delle applicazioni NubArca | nulla — nessun server, nessuna copia, nessuna telemetria |
| **Gestore** | chi amministra il server NubArca a cui accedi | il tuo account e tutti i tuoi contenuti |

Ai sensi del GDPR il **gestore è il titolare del trattamento**. Se il server lo
amministri tu, il titolare dei tuoi dati sei tu. Lo sviluppatore non è un
responsabile del trattamento per tuo conto e non riceve mai i tuoi contenuti. Le
richieste sui tuoi dati — accesso, esportazione, rettifica, cancellazione — vanno
rivolte al tuo gestore, non allo sviluppatore.

## Cosa raccoglie lo sviluppatore

**Nulla.**

Le applicazioni non contengono analytics, non contengono crash reporting, non
contengono pubblicità, non contengono SDK di attribuzione né alcun servizio di
terze parti che osservi l'uso. Non è una dichiarazione di intenti: è una
proprietà della build, ed è verificabile — l'elenco delle dipendenze pubblicate
dei client telefono, TV e web non contiene alcun componente di quel tipo.

Le app non effettuano alcuna richiesta di rete diversa da quelle verso
l'indirizzo del server che indichi tu.

## Cosa legge l'app sul tuo dispositivo

**Foto e video — solo se attivi la sincronizzazione, e solo quelli che scegli.**

L'app Android richiede l'accesso granulare a foto e video
(`READ_MEDIA_IMAGES`, `READ_MEDIA_VIDEO`). Viene richiesto quando attivi la
sincronizzazione, mai all'avvio, ed esiste per un solo scopo: caricare sul tuo
server i contenuti che selezioni. I contenuti selezionati non vengono letti per
altre ragioni, non vengono analizzati sul dispositivo e non vengono inviati
altrove.

L'app conserva inoltre, solo sul dispositivo:

* il **cookie di sessione**, nell'Android Keystore / iOS Keychain, vincolato a
  questo dispositivo e accessibile solo a schermo sbloccato;
* l'**indirizzo del server** a cui hai effettuato l'ultimo accesso, per
  precompilare la schermata di login;
* la tua **scelta del tema**;
* un **registro di sincronizzazione** di ciò che è già stato caricato, perché un
  nuovo tentativo non produca duplicati.

Il backup Android è disattivato per l'app (`allowBackup="false"`): nulla di tutto
questo può uscire dal telefono dentro un backup non cifrato.

## Cosa conserva il server

Questi dati sono detenuti dal tuo gestore, sulla sua macchina:

* **Account**: indirizzo email, nome visualizzato, l'*hash* della password (mai
  la password), lingua e preferenze di interfaccia.
* **File**: le foto, i video e i documenti che carichi, conservati come blob
  immutabili indirizzati per contenuto, insieme ai nomi di cartella e di file che
  scegli.
* **Metadati letti dai tuoi file**: EXIF, inclusa la **data di scatto** e, se la
  fotocamera l'ha registrata, le **coordinate GPS**.
* **Artefatti derivati**: miniature e anteprime.
* **Dati derivati dall'AI, solo se il gestore ha abilitato l'AI** — che è
  disattivata per impostazione predefinita: testo estratto (OCR), didascalie e
  descrizioni, embedding visivi e semantici, rilevamento volti, raggruppamento
  volti ed etichette.
* **Registri di audit** su caricamenti, download, cancellazioni e creazione o
  revoca delle condivisioni.
* **Log del server**, che possono includere indirizzi IP, come qualunque server
  web.

## A chi appartengono i dati derivati

**Chi possiede un file possiede tutto ciò che ne deriva** — EXIF, GPS, data di
scatto, testo estratto, didascalie, embedding, volti, raggruppamenti ed
etichette.

Non è una semplice affermazione, è applicato:

* i dati derivati non sono mai esposti a un altro utente;
* non esiste ricerca tra proprietari diversi, né raggruppamento di volti tra
  proprietari diversi;
* gli embedding grezzi e i payload grezzi dei modelli non sono mai esposti
  tramite API, riga di comando, log o diagnostica;
* GPS e data di scatto sono disponibili nelle tue viste private e **non** sono
  inclusi nelle condivisioni pubbliche né in aggregati che potrebbero rivelarli.

## Condivisione — cosa vede davvero il destinatario

La condivisione è sempre un atto deliberato. Esistono tre meccanismi, e ciascuno
mostra meno di quanto ci si aspetterebbe:

**Condivisione di album con un altro account.** Inviti un indirizzo email
esatto; non esiste una rubrica utenti né un completamento automatico, quindi il
server non può essere usato per scoprire chi ha un account. Il destinatario vede
i contenuti dell'album e il nome visualizzato del proprietario. Non vede mai gli
indirizzi email né gli identificativi degli altri membri, e il proprietario vede
solo un indirizzo **mascherato** dei propri membri.

**Link pubblici.** Un link porta con sé un token non indovinabile, è revocabile
in qualsiasi momento e può avere una scadenza. I contenuti raggiunti da un link
pubblico deliberatamente **non portano il nome del file** — un nome di file è
testo libero scritto da te e può contenere il nome di una persona — e non
portano GPS, data di scatto, dati derivati dall'AI né l'identità di chi ha
contribuito.

**Modalità Party.** Quando la attivi per un album, gli ospiti in possesso del
link possono guardare e, se lo consenti, caricare e scrivere messaggi, secondo le
impostazioni di approvazione che scegli. Gli ospiti vedono l'album, non la tua
libreria.

Nulla è condiviso per impostazione predefinita. Nulla è pubblico se non lo rendi
tale.

## Sicurezza

* Le build di produzione **richiedono HTTPS** e si rifiutano di essere compilate
  contro un'origine in chiaro; il traffico non cifrato è disabilitato nei binari
  distribuiti.
* I cookie di sessione sono HTTP-only.
* Le password sono conservate solo come hash.
* Gli endpoint di autenticazione e di condivisione pubblica hanno limiti di
  frequenza.
* Ogni download è autorizzato centralmente, a ogni richiesta.
* Percorsi fisici di archiviazione, hash dei contenuti, token di condivisione e
  metadati grezzi non sono mai esposti tramite API, log o diagnostica.

## Aggiornamenti over-the-air (NubArca TV)

L'applicazione TV può ricevere aggiornamenti del proprio livello **JavaScript**
dallo stesso server a cui accedi. Quei pacchetti sono firmati e vengono
pubblicati dal tuo gestore; lo sviluppatore non può inviare codice al tuo
dispositivo, e per questa via non viene mai sostituito codice nativo.

## Conservazione e cancellazione

La conservazione è una decisione del tuo gestore, perché i dati stanno sulla sua
macchina. La cancellazione di un file rimuove con sé i suoi artefatti derivati:
un'anteprima o un indice non sopravvivono mai al contenuto da cui sono stati
generati. Per cancellare un account, rivolgiti al gestore. Se il gestore sei tu,
cancellare i dati sul tuo server li cancella del tutto, senza copie altrove.

## Minori

NubArca non è rivolta ai minori e non tratta consapevolmente dati che li
riguardino.

## I tuoi diritti

Ai sensi del GDPR hai diritto di accesso, rettifica, cancellazione, limitazione,
portabilità e opposizione. Esercitali nei confronti del **gestore del server che
utilizzi**, che detiene i dati. Se il server lo amministri tu, hai già accesso
diretto e completo a tutto ciò che questo documento descrive.

## Modifiche a questa informativa

Le modifiche sostanziali saranno pubblicate in questo documento, aggiornando la
data in cima. La versione applicabile a una release è quella presente nel
repository di quella release.

## Contatti

Domande sulle applicazioni: `<CONTACT_EMAIL>`.

Domande sui tuoi dati: il tuo gestore — la persona o l'organizzazione al cui
server accedi.
