using System;
using System.Collections.Generic;
using System.Reflection;
using N2.Persistence.Finder;
using N2.Workflow;

namespace N2.Persistence
{
	/// <summary>
	/// Handles saving and restoring versions of items.
	/// </summary>
	public class VersionManager : IVersionManager
	{
        readonly IRepository<int, ContentItem> itemRepository;
		readonly IItemFinder finder;
        readonly StateChanger stateChanger;

		public VersionManager(IRepository<int, ContentItem> itemRepository, IItemFinder finder, StateChanger stateChanger)
		{
			this.itemRepository = itemRepository;
			this.finder = finder;
            this.stateChanger = stateChanger;
		}

		#region Versioning Methods
		/// <summary>Creates a version of the item. This must be called before the item item is modified to save a version before modifications.</summary>
		/// <param name="item">The item to create a old version of.</param>
		/// <returns>The old version.</returns>
		public virtual ContentItem SaveVersion(ContentItem item)
		{
			CancellableItemEventArgs args = new CancellableItemEventArgs(item);
			if (ItemSavingVersion != null)
				ItemSavingVersion.Invoke(this, args);
			if (!args.Cancel)
			{
				item = args.AffectedItem;
                
				ContentItem oldVersion = item.Clone(false);
                if(item.State == ContentState.Published)
                    stateChanger.ChangeTo(oldVersion, ContentState.Unpublished);
                else
                    stateChanger.ChangeTo(oldVersion, ContentState.Draft);
				oldVersion.Expires = Utility.CurrentTime().AddSeconds(-1);
				oldVersion.Updated = Utility.CurrentTime().AddSeconds(-1);
				oldVersion.Parent = null;
				oldVersion.VersionOf = item;
				if (item.Parent != null)
					oldVersion["ParentID"] = item.Parent.ID;
                itemRepository.SaveOrUpdate(oldVersion);

				if (ItemSavedVersion != null)
					ItemSavedVersion.Invoke(this, new ItemEventArgs(oldVersion));

				return oldVersion;
			}
			return null;
		}

        /// <summary>Update a page version with another, i.e. save a version of the current item and replace it with the replacement item. Returns a version of the previously published item.</summary>
        /// <param name="currentItem">The item that will be stored as a previous version.</param>
        /// <param name="replacementItem">The item that will take the place of the current item using it's ID. Any saved version of this item will not be modified.</param>
        /// <returns>A version of the previously published item.</returns>
        public virtual ContentItem ReplaceVersion(ContentItem currentItem, ContentItem replacementItem)
        {
            return ReplaceVersion(currentItem, replacementItem, true);
        }

		/// <summary>Update a page version with another, i.e. save a version of the current item and replace it with the replacement item. Returns a version of the previously published item.</summary>
		/// <param name="currentItem">The item that will be stored as a previous version.</param>
		/// <param name="replacementItem">The item that will take the place of the current item using it's ID. Any saved version of this item will not be modified.</param>
        /// <param name="storeCurrentVersion">Create a copy of the currently published version before overwriting it.</param>
        /// <returns>A version of the previously published item or the current item when storeCurrentVersion is false.</returns>
		public virtual ContentItem ReplaceVersion(ContentItem currentItem, ContentItem replacementItem, bool storeCurrentVersion)
		{
			CancellableDestinationEventArgs args = new CancellableDestinationEventArgs(currentItem, replacementItem);
			if (ItemReplacingVersion != null)
				ItemReplacingVersion.Invoke(this, args);
			if (!args.Cancel)
			{
				currentItem = args.AffectedItem;
				replacementItem = args.Destination;

				using (ITransaction transaction = itemRepository.BeginTransaction())
				{
                    if (storeCurrentVersion)
                    {
                        ContentItem versionOfCurrentItem = SaveVersion(currentItem); //TODO: remove?

                        Replace(currentItem, replacementItem);

                        transaction.Commit();
                        return versionOfCurrentItem;
                    }
                    else
                    {
                        Replace(currentItem, replacementItem);

                        transaction.Commit();
                        return currentItem;
                    }
				}
			}
			return currentItem;
		}

        private void Replace(ContentItem currentItem, ContentItem replacementItem)
        {
            ClearAllDetails(currentItem);

            ((IUpdatable<ContentItem>)currentItem).UpdateFrom(replacementItem);

            currentItem.Updated = Utility.CurrentTime();
            itemRepository.Update(currentItem);

            if (ItemReplacedVersion != null)
                ItemReplacedVersion.Invoke(this, new ItemEventArgs(replacementItem));

            itemRepository.Flush();
        }

		#region ReplaceVersion Helper Methods

		private void ClearAllDetails(ContentItem item)
		{
			item.Details.Clear();

			foreach (Details.DetailCollection collection in item.DetailCollections.Values)
			{
				collection.Details.Clear();
			}
			item.DetailCollections.Clear();
		}
		#endregion

		/// <summary>Retrieves all versions of an item including the master version.</summary>
		/// <param name="publishedItem">The item whose versions to get.</param>
		/// <returns>A list of versions of the item.</returns>
		public virtual IList<ContentItem> GetVersionsOf(ContentItem publishedItem)
		{
			return GetVersionsQuery(publishedItem)
				.Select();
		}

		/// <summary>Retrieves all versions of an item including the master version.</summary>
		/// <param name="publishedItem">The item whose versions to get.</param>
		/// <param name="count">The number of versions to get.</param>
		/// <returns>A list of versions of the item.</returns>
		public virtual IList<ContentItem> GetVersionsOf(ContentItem publishedItem, int count)
		{
			return GetVersionsQuery(publishedItem)
				.MaxResults(count)
				.Select();
		}

		private IQueryEnding GetVersionsQuery(ContentItem publishedItem)
		{
			return finder.Where.VersionOf.Eq(publishedItem)
				.Or.ID.Eq(publishedItem.ID)
				.OrderBy.VersionIndex.Desc;
		}

		public virtual void TrimVersionCountTo(ContentItem publishedItem, int maximumNumberOfVersions)
		{
			if (maximumNumberOfVersions < 0) throw new ArgumentOutOfRangeException("maximumNumberOfVersions");
			if (maximumNumberOfVersions == 0) return;

			IList<ContentItem> versions = GetVersionsOf(publishedItem);
			versions.Remove(publishedItem);
			int max = maximumNumberOfVersions - 1;

			if (versions.Count <= max) return;

			using (ITransaction transaction = itemRepository.BeginTransaction())
			{
				for (int i = max; i < versions.Count; i++)
				{
					this.itemRepository.Delete(versions[i]);
				}
				itemRepository.Flush();
				transaction.Commit();
			}
		}

		#endregion


		/// <summary>Occurs before an item is saved</summary>
		public event EventHandler<CancellableItemEventArgs> ItemSavingVersion;
		/// <summary>Occurs before an item is saved</summary>
		public event EventHandler<ItemEventArgs> ItemSavedVersion;
		/// <summary>Occurs before an item is saved</summary>
		public event EventHandler<CancellableDestinationEventArgs> ItemReplacingVersion;
		/// <summary>Occurs before an item is saved</summary>
		public event EventHandler<ItemEventArgs> ItemReplacedVersion;
	}
}
