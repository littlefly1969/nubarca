# Faces and people

How to use face recognition in NubArca: find the people in your photos and
videos, give them names, and keep the groups tidy.

This page describes the **product**. It shows none of your data: your faces,
people and photos stay private and visible only to you.

## Where it lives

In the navigation menu, **Volti** ("Faces") opens `/people`. It is a private
page: it only ever shows faces found in your own library.

The page is divided into sections, and the selected section lives in the URL
(`/people?tab=…`), so it survives a reload, works with the browser's Back
button, and can be bookmarked.

The sections are **Gruppi suggeriti** (Suggested groups), **Persone** (People),
**Volti non assegnati** (Unassigned faces), **Foto da rivedere** (Photos to
review), **Da revisionare** (To review), **Volti nei video** (Faces in videos),
**Ignorati** (Ignored) and — for whoever administers the installation —
**Impostazioni Face AI** (Face AI settings).

## The normal workflow: start from Suggested groups

**Gruppi suggeriti** is the section you land on, and it is where the work
normally starts. NubArca groups faces that look alike on its own and offers each
group with a cover face, how many faces it contains, and a confidence
percentage.

A suggestion is only a suggestion: **nothing becomes a person until you confirm
it**. NubArca never assigns a name by itself and never creates a person
automatically.

To work through a group:

1. open **Rivedi gruppo** ("Review group") to look at the faces it contains, one
   at a time;
2. if the group is coherent, type the name into **Assegna nome** ("Assign name")
   and press **Assegna** ("Assign"): the group becomes a person with that name;
3. if that person already exists, use **oppure aggiungi a…** ("or add to…") and
   pick the name from the list instead — the group's faces are added to the
   existing person rather than creating a second one;
4. if the group is not interesting — strangers, background, a crowd shot — use
   **Ignora gruppo** ("Ignore group"). NubArca asks for confirmation and moves
   every face in the group to **Ignorati**, where you can always restore them.

## People

**Persone** lists the people you have already named, with their cover photo and
how many faces are confirmed. Until you have created one, the section says
"Nessuna persona ancora" — no people yet — and points you at the suggested
groups.

**Cerca persona** ("Search person") filters the list by name once there are many
of them.

Opening a person shows their photos and videos, and lets you:

- **Rinomina** ("Rename") — change the name;
- **Rimuovi volto** ("Remove face") — take out a face assigned by mistake: it
  goes back to the unassigned faces and can be reassigned;
- **Cerca volti simili** ("Search similar faces") — look for other faces that
  resemble this person, with a **Soglia similarità** (similarity threshold) you
  can raise or lower, and add them with **Aggiungi** ("Add");
- **Rimuovi persona** ("Remove person") — delete the person: the faces go back
  to unassigned, and no photo is touched.

A person is represented by a small set of **Volti di riferimento** (reference
faces) chosen from the assignments you confirmed. Similar-face search uses them,
and **Ricalcola riferimenti** rebuilds them.

## Unassigned faces

**Volti non assegnati** is the flat list of every face NubArca has found that
does not belong to a person yet, one face at a time.

It is the right section when you are looking for **one** particular face rather
than working group by group. From here you can assign the face to an existing
person, create a new one, or ignore it.

## Photos to review

**Foto da rivedere** looks at the same work from the other side: instead of one
face at a time, it shows the **photos** that still contain undecided faces, so
you can open one and finish it completely.

Inside a photo you move with **Volto precedente** and **Volto successivo**
(previous/next face), see where you are ("Volto 2 di 5"), and for each face you
can assign it, skip it with **Salta volto**, or ignore it. **Ignora tutti i
volti non assegnati** closes out every remaining face in that photo at once.

The two sections do not replace each other: they answer different questions.

## To review

**Da revisionare** collects the groups NubArca formed with less confidence. They
are worked exactly like the suggested groups — review, assign or ignore — but
they are worth a closer look before you put a name on one.

## Faces in videos

**Volti nei video** shows the faces detected in your videos. Suggestions are
advisory here too: a face becomes a person only when you confirm it. You can
confirm the offered suggestion, pick the person yourself with **Assegna a**, or
**Ignora**.

Once confirmed, the video appears among that person's videos and you can open it
at the moment they appear.

Video face analysis can be turned off by whoever administers the installation.
When it is off, everything already recognised stays visible and usable — only
the analysis of new videos stops.

## Ignored

**Ignorati** holds the faces you have set aside. They are not deleted:
**Ripristina** ("Restore") returns them to the unassigned faces, where they can
be reassigned or offered again by the automatic grouping.

Ignoring is therefore a reversible decision, and it is the right way to clear
strangers and false positives out of the way without losing anything.

## Face AI settings

**Impostazioni Face AI** is an administrative section: it appears only to
someone holding the installation's administration permission.

It shows whether face detection, embeddings and clustering are enabled, which
profile is in use, and the thresholds governing how wide or tight the groups are
and where similar-face search starts. Background recomputations are started from
there.

You do not need to touch it to use faces day to day.

## When face recognition is unavailable

Face recognition is optional and may not be enabled on this installation.

When it is not, NubArca says so rather than showing an empty page: the faces
sections report "Il riconoscimento dei volti non è attivo" (face recognition is
not enabled), and similar-face search answers "Ricerca volti non disponibile in
questo ambiente". It is not an error and nothing is lost: whoever administers
the installation can enable the feature and have the library analysed.

If recognition is enabled but no groups have appeared yet, the library analysis
is usually still running.

## Privacy

Everything about faces is **private and yours**: the detected faces, the people
you created, the names you chose and the groupings are visible only to you.

Faces and people never appear in public shares, and NubArca never relates people
across different users' libraries.
